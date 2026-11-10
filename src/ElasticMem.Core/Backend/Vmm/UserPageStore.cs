using System.Runtime.InteropServices;
namespace ElasticMem.Core.Backend.Vmm;

// 4096-byte page codec. TryCompress returns false when the page cannot be represented in fewer bytes.
public interface IPageCodec
{
    string Name { get; }
    bool TryCompress(ReadOnlySpan<byte> page, Span<byte> destination, out int written);
    bool TryDecompress(ReadOnlySpan<byte> source, Span<byte> page, out int written);
}

public sealed class PageNotBeneficialException : IOException
{
    public PageNotBeneficialException() : base("Compressed page is not smaller than a raw page; original must stay resident") { }
}

public sealed record UserStoreStats(int Pages, long PayloadBytes, long LedgerBytes, long QuotaBytes, long Puts, long Gets, long Deletes, long RejectedNotBeneficial);

/// <summary>Общий интерфейс хранилища сжатых страниц (per-page либо с дедупликацией) для CompressedPageTable.</summary>
public interface IUserPageStore : IVmCompressedStore, IDisposable
{
    string CodecName { get; }
    UserStoreStats Stats();
}

// User-mode counterpart of the SYS em_store semantics: compressed-only, per-key generation, CRC check,
// quota on payload+24 bytes per entry (same ledger rule as the driver). Payload lives in the VMM process
// native heap, so it IS charged to the process private commit; nothing is hidden in kernel pool.
public sealed unsafe class UserPageStore : IUserPageStore
{
    public const int PageBytes = 4096, EntryOverheadBytes = 24;
    struct Entry { public IntPtr Data; public int Size; public uint Crc; public ulong Generation; }
    readonly IPageCodec codec; readonly Entry[] entries; readonly long quota; readonly object gate = new();
    readonly byte[] scratch = new byte[PageBytes];
    long ledger, payload, puts, gets, deletes, rejected; int pages; ulong nextGeneration; bool disposed;

    public UserPageStore(IPageCodec codec, int capacity, long quotaBytes)
    {
        this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
        if (capacity is < 1 or > 1048576) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (quotaBytes < PageBytes) throw new ArgumentOutOfRangeException(nameof(quotaBytes));
        entries = new Entry[capacity]; quota = quotaBytes;
    }
    public string CodecName => codec.Name;
    void Key(uint key) { ObjectDisposedException.ThrowIf(disposed, this); if (key >= entries.Length) throw new ArgumentOutOfRangeException(nameof(key)); }

    public VmStoreReceipt Put(uint key, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes); if (bytes.Length != PageBytes) throw new ArgumentException("Exact 4096-byte page required", nameof(bytes));
        lock (gate)
        {
            Key(key);
            try
            {
                if (!codec.TryCompress(bytes, scratch, out int size) || size <= 0 || size + EntryOverheadBytes >= PageBytes) { rejected++; throw new PageNotBeneficialException(); }
                ref var old = ref entries[key]; long oldLedger = old.Data != IntPtr.Zero ? old.Size + EntryOverheadBytes : 0;
                if (ledger - oldLedger + size + EntryOverheadBytes > quota) throw new IOException("User page store quota exceeded; original stays resident");
                if (nextGeneration == ulong.MaxValue) throw new IOException("Generation space exhausted");
                IntPtr data = (IntPtr)NativeMemory.Alloc((nuint)size);
                scratch.AsSpan(0, size).CopyTo(new Span<byte>((void*)data, size));
                uint crc = Crc32(bytes);
                if (old.Data != IntPtr.Zero) Free(ref old); else pages++;
                old = new Entry { Data = data, Size = size, Crc = crc, Generation = ++nextGeneration };
                ledger += size + EntryOverheadBytes; payload += size; puts++;
                return new(old.Generation, (uint)size, (uint)(size + EntryOverheadBytes));
            }
            finally { Array.Clear(scratch); }
        }
    }

    public byte[] Get(uint key, ulong generation)
    {
        lock (gate)
        {
            Key(key); var e = entries[key];
            if (e.Data == IntPtr.Zero) throw new KeyNotFoundException("Page not stored");
            if (generation == 0 || e.Generation != generation) throw new InvalidDataException("Stale page generation");
            var page = new byte[PageBytes];
            if (!codec.TryDecompress(new ReadOnlySpan<byte>((void*)e.Data, e.Size), page, out int written) || written != PageBytes || Crc32(page) != e.Crc)
            { Array.Clear(page); throw new InvalidDataException("Corrupt stored page"); }
            gets++; return page;
        }
    }

    public bool Delete(uint key, ulong generation)
    {
        lock (gate)
        {
            Key(key); ref var e = ref entries[key];
            if (e.Data == IntPtr.Zero) return false;
            if (e.Generation != generation) throw new InvalidDataException("Stale page generation");
            Free(ref e); pages--; deletes++; return true;
        }
    }

    void Free(ref Entry e)
    {
        new Span<byte>((void*)e.Data, e.Size).Clear(); NativeMemory.Free((void*)e.Data);
        ledger -= e.Size + EntryOverheadBytes; payload -= e.Size; e = default;
    }

    public UserStoreStats Stats() { lock (gate) return new(pages, payload, ledger, quota, puts, gets, deletes, rejected); }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            for (int i = 0; i < entries.Length; i++) if (entries[i].Data != IntPtr.Zero) Free(ref entries[i]);
            pages = 0; disposed = true;
        }
    }

    static readonly uint[] CrcTable = BuildTable();
    static uint[] BuildTable() { var t = new uint[256]; for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (0u - (c & 1u))); t[i] = c; } return t; }
    public static uint Crc32(ReadOnlySpan<byte> data) { uint c = 0xFFFFFFFFu; foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8); return ~c; }
}
