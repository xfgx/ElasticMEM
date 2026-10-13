using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ElasticMem.Core;

public enum BlockFormat : byte
{
    Raw = 0,       // без сжатия
    Zero = 1,      // весь блок — нули
    BitPack = 2,   // узкие числа: упаковка по необходимому числу бит
    BaseDelta = 3, // близкие значения: общая база + смещения (+ исключения)
    Sparse = 4,    // много нулей: маска ненулевых слов + их значения
    Lz = 5,        // словарное сжатие (Deflate)
}

/// <summary>Кандидат представления блока (ещё не построенный).</summary>
public readonly record struct Candidate(BlockFormat Format, int Bits, ulong Base, int Headroom, int Exceptions, int SizeBytes);

/// <summary>Сжатое (холодное) представление одного микроблока. Всё без потерь.</summary>
public sealed class EncodedBlock
{
    public BlockFormat Format;
    public int Bits;
    public ulong Base;
    public int Headroom;
    public byte[] Payload = Array.Empty<byte>();
    public Dictionary<int, ulong>? Exceptions; // BitPack/BaseDelta: значения, не влезшие в разрядность
    public int MaxExceptions;
    public ulong[]? SparseValues;               // Sparse: ненулевые слова по порядку

    public int SizeBytes => Format switch
    {
        BlockFormat.Zero => 1,
        BlockFormat.BitPack or BlockFormat.BaseDelta => BlockCodec.PackedHeader + Payload.Length + (Exceptions?.Count ?? 0) * BlockCodec.ExceptionBytes,
        BlockFormat.Sparse => BlockCodec.SparseHeader + Payload.Length + (SparseValues?.Length ?? 0) * 8,
        BlockFormat.Lz => 1 + Payload.Length,
        _ => Payload.Length,
    };

    public bool SupportsWordAccess => Format is BlockFormat.Zero or BlockFormat.BitPack or BlockFormat.BaseDelta or BlockFormat.Sparse;
}

public sealed class CodecOptions
{
    public bool EnablePacked = true;
    public bool EnableSparse = true;
    public bool EnableLz = true;
    public int[] Headrooms = { 0 };        // запас разрядности (бит) для BitPack/BaseDelta
    public int ExceptionDivisor = 16;      // до n/16 исключений в BaseDelta
    public CompressionLevel LzLevel = CompressionLevel.Fastest;
}

/// <summary>Кодеки микроблоков. Блок интерпретируется как массив 64-битных слов (little-endian).</summary>
public static class BlockCodec
{
    public const int PackedHeader = 12;  // формат, биты, база, счётчик исключений
    public const int ExceptionBytes = 10; // индекс(2) + значение(8)
    public const int SparseHeader = 3;

    [ThreadStatic] private static MemoryStream? _ms;

    public static List<Candidate> Candidates(ReadOnlySpan<byte> block, CodecOptions opt)
    {
        var list = new List<Candidate>(8);
        int size = block.Length;
        list.Add(new Candidate(BlockFormat.Raw, 0, 0, 0, 0, size));
        if (size % 8 != 0) { if (opt.EnableLz) list.Add(LzCandidate(block, opt)); return list; }

        var w = MemoryMarshal.Cast<byte, ulong>(block);
        int n = w.Length;
        ulong min = ulong.MaxValue, max = 0;
        int nz = 0;
        for (int i = 0; i < n; i++)
        {
            ulong v = w[i];
            if (v < min) min = v;
            if (v > max) max = v;
            if (v != 0) nz++;
        }
        if (nz == 0) { list.Add(new Candidate(BlockFormat.Zero, 0, 0, 0, 0, 1)); return list; }

        if (opt.EnablePacked)
        {
            int bitsAbs = BitPack.BitsNeeded(max);
            int bitsRel = BitPack.BitsNeeded(max - min);
            foreach (int h in opt.Headrooms)
            {
                int b = bitsAbs + h;
                if (b < 64) list.Add(new Candidate(BlockFormat.BitPack, b, 0, h, 0, PackedHeader + BitPack.PackedBytes(n, b)));
                if (min != 0)
                {
                    int b2 = bitsRel + h;
                    if (b2 < 64) list.Add(new Candidate(BlockFormat.BaseDelta, b2, min, h, 0, PackedHeader + BitPack.PackedBytes(n, b2)));
                }
            }
            // Вариант с исключениями: выбросы хранятся отдельно, основная масса — узко.
            int k = n / Math.Max(2, opt.ExceptionDivisor);
            if (k > 0)
            {
                var sorted = w.ToArray();
                Array.Sort(sorted);
                ulong lo = sorted[k / 2];               // часть выбросов снизу
                ulong hi = sorted[n - 1 - (k - k / 2)]; // часть выбросов сверху
                int bitsE = BitPack.BitsNeeded(hi - lo);
                foreach (int h in opt.Headrooms)
                {
                    int b = bitsE + h;
                    if (b >= 64) continue;
                    ulong lim = BitPack.MaxValue(b);
                    int exc = 0;
                    for (int i = 0; i < n; i++) if (w[i] < lo || w[i] - lo > lim) exc++;
                    if (exc == 0) continue;
                    int sz = PackedHeader + BitPack.PackedBytes(n, b) + exc * ExceptionBytes;
                    list.Add(new Candidate(BlockFormat.BaseDelta, b, lo, h, exc, sz));
                }
            }
        }
        if (opt.EnableSparse && nz < n / 2)
            list.Add(new Candidate(BlockFormat.Sparse, 0, 0, 0, 0, SparseHeader + (n + 7) / 8 + nz * 8));
        if (opt.EnableLz) list.Add(LzCandidate(block, opt));
        return list;
    }

    private static Candidate LzCandidate(ReadOnlySpan<byte> block, CodecOptions opt)
    {
        int len = LzCompress(block, opt.LzLevel).Length;
        return new Candidate(BlockFormat.Lz, 0, 0, 0, 0, 1 + len);
    }

    public static byte[] LzCompress(ReadOnlySpan<byte> data, CompressionLevel level)
    {
        var ms = _ms ??= new MemoryStream();
        ms.SetLength(0);
        using (var ds = new DeflateStream(ms, level, leaveOpen: true)) ds.Write(data);
        return ms.ToArray();
    }

    public static void LzDecompress(byte[] payload, Span<byte> dst)
    {
        using var ds = new DeflateStream(new MemoryStream(payload), CompressionMode.Decompress);
        int read = 0;
        while (read < dst.Length)
        {
            int r = ds.Read(dst[read..]);
            if (r == 0) throw new InvalidDataException("Повреждён Lz-блок");
            read += r;
        }
    }

    public static EncodedBlock Build(ReadOnlySpan<byte> block, Candidate c, CodecOptions opt)
    {
        var e = new EncodedBlock { Format = c.Format, Bits = c.Bits, Base = c.Base, Headroom = c.Headroom };
        switch (c.Format)
        {
            case BlockFormat.Raw:
                e.Payload = block.ToArray();
                break;
            case BlockFormat.Zero:
                break;
            case BlockFormat.BitPack:
            case BlockFormat.BaseDelta:
            {
                var w = MemoryMarshal.Cast<byte, ulong>(block);
                int n = w.Length;
                e.Payload = new byte[BitPack.PackedBytes(n, c.Bits)];
                e.MaxExceptions = Math.Max(c.Exceptions, n / Math.Max(2, opt.ExceptionDivisor));
                ulong lim = BitPack.MaxValue(c.Bits);
                for (int i = 0; i < n; i++)
                {
                    ulong v = w[i];
                    if (v >= c.Base && v - c.Base <= lim) BitPack.Set(e.Payload, i, c.Bits, v - c.Base);
                    else (e.Exceptions ??= new Dictionary<int, ulong>())[i] = v;
                }
                break;
            }
            case BlockFormat.Sparse:
            {
                var w = MemoryMarshal.Cast<byte, ulong>(block);
                int n = w.Length;
                e.Payload = new byte[(n + 7) / 8];
                var vals = new List<ulong>();
                for (int i = 0; i < n; i++)
                    if (w[i] != 0) { e.Payload[i >> 3] |= (byte)(1 << (i & 7)); vals.Add(w[i]); }
                e.SparseValues = vals.ToArray();
                break;
            }
            case BlockFormat.Lz:
                e.Payload = LzCompress(block, opt.LzLevel);
                break;
        }
        return e;
    }

    public static void Decode(EncodedBlock e, Span<byte> dst)
    {
        switch (e.Format)
        {
            case BlockFormat.Raw: e.Payload.CopyTo(dst); break;
            case BlockFormat.Zero: dst.Clear(); break;
            case BlockFormat.Lz: LzDecompress(e.Payload, dst); break;
            default:
            {
                var w = MemoryMarshal.Cast<byte, ulong>(dst);
                if (e.Format == BlockFormat.Sparse)
                {
                    int r = 0;
                    for (int i = 0; i < w.Length; i++)
                        w[i] = (e.Payload[i >> 3] & (1 << (i & 7))) != 0 ? e.SparseValues![r++] : 0;
                }
                else
                {
                    for (int i = 0; i < w.Length; i++) w[i] = e.Base + BitPack.Get(e.Payload, i, e.Bits);
                    if (e.Exceptions != null) foreach (var kv in e.Exceptions) w[kv.Key] = kv.Value;
                }
                break;
            }
        }
    }

    private static int SparseRank(byte[] bitmap, int index)
    {
        int r = 0, full = index >> 3;
        for (int i = 0; i < full; i++) r += BitOperations.PopCount(bitmap[i]);
        r += BitOperations.PopCount((uint)(bitmap[full] & ((1 << (index & 7)) - 1)));
        return r;
    }

    /// <summary>Прямое чтение слова без распаковки всего блока.</summary>
    public static ulong GetWord(EncodedBlock e, int i)
    {
        switch (e.Format)
        {
            case BlockFormat.Zero: return 0;
            case BlockFormat.BitPack:
            case BlockFormat.BaseDelta:
                if (e.Exceptions != null && e.Exceptions.TryGetValue(i, out var ex)) return ex;
                return e.Base + BitPack.Get(e.Payload, i, e.Bits);
            case BlockFormat.Sparse:
                if ((e.Payload[i >> 3] & (1 << (i & 7))) == 0) return 0;
                return e.SparseValues![SparseRank(e.Payload, i)];
            default: throw new InvalidOperationException("Формат не поддерживает доступ по словам");
        }
    }

    public enum SetResult { InPlace, NeedsException, Impossible }

    public static SetResult CanSetWord(EncodedBlock e, int i, ulong v)
    {
        switch (e.Format)
        {
            case BlockFormat.Zero: return v == 0 ? SetResult.InPlace : SetResult.Impossible;
            case BlockFormat.BitPack:
            case BlockFormat.BaseDelta:
                if (v >= e.Base && v - e.Base <= BitPack.MaxValue(e.Bits)) return SetResult.InPlace;
                if (e.Exceptions != null && e.Exceptions.ContainsKey(i)) return SetResult.InPlace;
                return SetResult.NeedsException;
            case BlockFormat.Sparse:
                bool present = (e.Payload[i >> 3] & (1 << (i & 7))) != 0;
                return present == (v != 0) ? SetResult.InPlace : SetResult.Impossible;
            default: return SetResult.Impossible;
        }
    }

    /// <summary>Записывает слово прямо в сжатое представление (после успешной проверки CanSetWord).</summary>
    public static void SetWord(EncodedBlock e, int i, ulong v)
    {
        switch (e.Format)
        {
            case BlockFormat.Zero: return;
            case BlockFormat.BitPack:
            case BlockFormat.BaseDelta:
                if (v >= e.Base && v - e.Base <= BitPack.MaxValue(e.Bits))
                {
                    BitPack.Set(e.Payload, i, e.Bits, v - e.Base);
                    e.Exceptions?.Remove(i);
                }
                else (e.Exceptions ??= new Dictionary<int, ulong>())[i] = v;
                return;
            case BlockFormat.Sparse:
                if (v != 0) e.SparseValues![SparseRank(e.Payload, i)] = v;
                return;
        }
    }

    /// <summary>Минимальная разрядность смещений блока (для оценки роста диапазона).</summary>
    public static int RequiredBits(ReadOnlySpan<byte> block)
    {
        if (block.Length % 8 != 0) return 64;
        var w = MemoryMarshal.Cast<byte, ulong>(block);
        ulong min = ulong.MaxValue, max = 0;
        foreach (var v in w) { if (v < min) min = v; if (v > max) max = v; }
        return BitPack.BitsNeeded(max - min);
    }
}
