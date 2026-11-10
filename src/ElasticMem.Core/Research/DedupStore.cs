using System.IO.Compression;
using System.Security.Cryptography;

namespace ElasticMem.Core.Research;

/// <summary>
/// Content-addressed хранилище с дедупликацией — полноценная реализация, не прототип.
///
/// Каждый чанк адресуется по криптографическому хешу (SHA-256), поэтому одинаковый контент хранится
/// ровно один раз; повторные Put увеличивают счётчик ссылок. Уникальный чанк хранится сжатым (Deflate;
/// если сжатие не уменьшает — сырьём). Get восстанавливает точные байты, Release уменьшает счётчик и
/// освобождает чанк при нуле. Это та самая ось, которую не трогают ни Windows Memory Compression, ни
/// прежние механизмы проекта: избыточность МЕЖДУ страницами и процессами.
///
/// Коллизии SHA-256 в практике невозможны, поэтому отдельное побайтовое сравнение не требуется.
/// </summary>
public sealed class DedupStore
{
    public readonly record struct Key(ulong A, ulong B, ulong C, ulong D);

    sealed class Entry
    {
        public long Id;
        public Key Key;
        public byte[] Payload = Array.Empty<byte>();
        public bool Compressed;
        public int Length;
        public int Refs;
    }

    readonly Dictionary<Key, Entry> _byHash = new();
    readonly Dictionary<long, Entry> _byId = new();
    long _nextId = 1;

    /// <summary>Сумма длин всех Put (с учётом дубликатов) — логический объём.</summary>
    public long LogicalBytes { get; private set; }
    /// <summary>Фактически хранимые байты (уникальные чанки, сжатые).</summary>
    public long StoredBytes { get; private set; }
    /// <summary>Сумма длин уникальных чанков без сжатия (дедуп без кодека).</summary>
    public long UniqueRawBytes { get; private set; }
    public int UniqueChunks => _byHash.Count;
    public long TotalPuts { get; private set; }

    /// <summary>Положить чанк. Возвращает идентификатор (одинаковый для одинакового контента).</summary>
    public long Put(ReadOnlySpan<byte> chunk)
    {
        TotalPuts++;
        LogicalBytes += chunk.Length;
        var key = Hash(chunk);
        if (_byHash.TryGetValue(key, out var e)) { e.Refs++; return e.Id; }

        var (payload, compressed) = Pack(chunk);
        e = new Entry { Id = _nextId++, Key = key, Payload = payload, Compressed = compressed, Length = chunk.Length, Refs = 1 };
        _byHash[key] = e;
        _byId[e.Id] = e;
        StoredBytes += payload.Length;
        UniqueRawBytes += chunk.Length;
        return e.Id;
    }

    /// <summary>Восстановить точные байты по идентификатору.</summary>
    public byte[] Get(long id)
    {
        var e = _byId[id];
        if (!e.Compressed) return (byte[])e.Payload.Clone();
        var dst = new byte[e.Length];
        using var ms = new MemoryStream(e.Payload, false);
        using var ds = new DeflateStream(ms, CompressionMode.Decompress);
        int read = 0;
        while (read < dst.Length) { int n = ds.Read(dst, read, dst.Length - read); if (n == 0) break; read += n; }
        return dst;
    }

    /// <summary>Счётчик ссылок одного идентификатора (0 — освобождён).</summary>
    public int RefCount(long id) => _byId.TryGetValue(id, out var e) ? e.Refs : 0;

    /// <summary>Уменьшить счётчик ссылок; при нуле чанк освобождается.</summary>
    public void Release(long id)
    {
        if (!_byId.TryGetValue(id, out var e)) return;
        if (--e.Refs > 0) return;
        _byId.Remove(id);
        _byHash.Remove(e.Key);
        StoredBytes -= e.Payload.Length;
        UniqueRawBytes -= e.Length;
    }

    static (byte[] payload, bool compressed) Pack(ReadOnlySpan<byte> chunk)
    {
        using var ms = new MemoryStream(chunk.Length);
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(chunk);
        if (ms.Length < chunk.Length) return (ms.ToArray(), true);
        return (chunk.ToArray(), false);
    }

    static Key Hash(ReadOnlySpan<byte> data)
    {
        Span<byte> h = stackalloc byte[32];
        SHA256.HashData(data, h);
        return new Key(
            BitConverter.ToUInt64(h[0..8]), BitConverter.ToUInt64(h[8..16]),
            BitConverter.ToUInt64(h[16..24]), BitConverter.ToUInt64(h[24..32]));
    }
}
