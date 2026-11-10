using System.Security.Cryptography;

namespace ElasticMem.Core.Backend.Vmm;

/// <summary>
/// Хранилище сжатых страниц С ДЕДУПЛИКАЦИЕЙ по контенту. В отличие от <see cref="UserPageStore"/>,
/// одинаковые страницы (по SHA-256) хранятся один раз со счётчиком ссылок — это убирает избыточность
/// МЕЖДУ страницами, которую обычное per-page сжатие не видит. API совпадает с UserPageStore, так что
/// <see cref="ElasticMem.Core.CompressedPageTable"/> может работать поверх любого из них.
///
/// Сжатие — тем же <see cref="IPageCodec"/> (на Windows XPRESS). Несжимаемая страница отвергается
/// (PageNotBeneficialException), как и в UserPageStore, и остаётся резидентной. Квота считается только по
/// фактически хранимым (уникальным) байтам: дубликаты бесплатны.
/// </summary>
public sealed class DedupPageStore : IUserPageStore
{
    public const int PageBytes = 4096, EntryOverheadBytes = 24;

    readonly record struct Hash(ulong A, ulong B, ulong C, ulong D);

    sealed class Content { public byte[] Data = Array.Empty<byte>(); public int Refs; public uint Crc; }
    struct Slot { public bool Present; public Hash Key; public ulong Generation; }

    readonly IPageCodec codec;
    readonly Slot[] slots;
    readonly Dictionary<Hash, Content> content = new();
    readonly long quota;
    readonly object gate = new();
    readonly byte[] scratch = new byte[PageBytes];
    long ledger, payload, puts, gets, deletes, rejected;
    ulong nextGeneration;
    bool disposed;

    public DedupPageStore(IPageCodec codec, int capacity, long quotaBytes)
    {
        this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
        if (capacity is < 1 or > 1048576) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (quotaBytes < PageBytes) throw new ArgumentOutOfRangeException(nameof(quotaBytes));
        slots = new Slot[capacity]; quota = quotaBytes;
    }

    public string CodecName => codec.Name + "+dedup";

    void Key(uint key) { ObjectDisposedException.ThrowIf(disposed, this); if (key >= slots.Length) throw new ArgumentOutOfRangeException(nameof(key)); }

    public VmStoreReceipt Put(uint key, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length != PageBytes) throw new ArgumentException("Exact 4096-byte page required", nameof(bytes));
        lock (gate)
        {
            Key(key);
            try
            {
                if (!codec.TryCompress(bytes, scratch, out int size) || size <= 0 || size + EntryOverheadBytes >= PageBytes)
                { rejected++; throw new PageNotBeneficialException(); }
                if (nextGeneration == ulong.MaxValue) throw new IOException("Generation space exhausted");

                var h = HashOf(bytes);
                bool newContent = !content.TryGetValue(h, out var c);
                if (newContent && ledger + size + EntryOverheadBytes > quota)
                    throw new IOException("Dedup page store quota exceeded; original stays resident");

                // Освободить прежнее содержимое этого ключа (перезапись).
                ref var slot = ref slots[key];
                if (slot.Present) ReleaseContent(slot.Key);

                if (newContent)
                {
                    var data = new byte[size];
                    scratch.AsSpan(0, size).CopyTo(data);
                    c = new Content { Data = data, Refs = 0, Crc = Crc(bytes) };
                    content[h] = c;
                    ledger += size + EntryOverheadBytes; payload += size;
                }
                c!.Refs++;
                slot = new Slot { Present = true, Key = h, Generation = ++nextGeneration };
                puts++;
                return new VmStoreReceipt(slot.Generation, (uint)c.Data.Length, (uint)(c.Data.Length + EntryOverheadBytes));
            }
            finally { Array.Clear(scratch); }
        }
    }

    public byte[] Get(uint key, ulong generation)
    {
        lock (gate)
        {
            Key(key);
            ref var slot = ref slots[key];
            if (!slot.Present) throw new KeyNotFoundException("Page not stored");
            if (generation == 0 || slot.Generation != generation) throw new InvalidDataException("Stale page generation");
            var c = content[slot.Key];
            var page = new byte[PageBytes];
            if (!codec.TryDecompress(c.Data, page, out int written) || written != PageBytes || Crc(page) != c.Crc)
            { Array.Clear(page); throw new InvalidDataException("Corrupt stored page"); }
            gets++; return page;
        }
    }

    public bool Delete(uint key, ulong generation)
    {
        lock (gate)
        {
            Key(key);
            ref var slot = ref slots[key];
            if (!slot.Present) return false;
            if (slot.Generation != generation) throw new InvalidDataException("Stale page generation");
            ReleaseContent(slot.Key);
            slot = default; deletes++; return true;
        }
    }

    void ReleaseContent(Hash h)
    {
        if (!content.TryGetValue(h, out var c)) return;
        if (--c.Refs > 0) return;
        ledger -= c.Data.Length + EntryOverheadBytes; payload -= c.Data.Length;
        Array.Clear(c.Data);
        content.Remove(h);
    }

    public UserStoreStats Stats()
    {
        lock (gate)
        {
            int liveKeys = 0;
            foreach (var s in slots) if (s.Present) liveKeys++;
            return new UserStoreStats(liveKeys, payload, ledger, quota, puts, gets, deletes, rejected);
        }
    }

    /// <summary>Сколько ключей сейчас ссылается на хранимые уникальные страницы (для отчёта об экономии дедупом).</summary>
    public (int LiveKeys, int UniquePages) DedupInfo()
    {
        lock (gate)
        {
            int liveKeys = 0; foreach (var s in slots) if (s.Present) liveKeys++;
            return (liveKeys, content.Count);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            foreach (var c in content.Values) Array.Clear(c.Data);
            content.Clear(); disposed = true;
        }
    }

    static Hash HashOf(ReadOnlySpan<byte> data)
    {
        Span<byte> h = stackalloc byte[32];
        SHA256.HashData(data, h);
        return new Hash(BitConverter.ToUInt64(h[0..8]), BitConverter.ToUInt64(h[8..16]), BitConverter.ToUInt64(h[16..24]), BitConverter.ToUInt64(h[24..32]));
    }

    static uint Crc(ReadOnlySpan<byte> data) => UserPageStore.Crc32(data);
}
