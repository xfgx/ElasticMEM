using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;

namespace ElasticMem.Core.Research;

/// <summary>
/// Доказательство, что сжатие страниц работает по-настоящему, а не «в отчёте».
///
/// Все страницы упаковываются в ОДНУ арену вместе с индексом — это честный размер реального хранилища
/// (с накладными расходами, а не сумма длин сжатых буферов). Невыгодная страница хранится сырьём с флагом —
/// как поступило бы любое хранилище. Для каждой исходной страницы заранее считается SHA-256; после
/// восстановления из арены хеш обязан совпасть. Отрицательный контроль портит один байт в копии арены и
/// обязан обнаружить порчу — иначе проверка сама по себе ничего не доказывает.
///
/// Формат арены: [N × (int32 offset, int32 length | RAW-флаг)] [полезные нагрузки подряд].
/// Работает поверх Span, поэтому та же арена может лежать в нативной памяти (замер по счётчикам ОС).
/// </summary>
public static class CompressionProof
{
    public const int PageBytes = 4096, IndexEntryBytes = 8;
    const int RawFlag = unchecked((int)0x80000000);

    public sealed record Packed(byte[][] Payloads, bool[] Raw, byte[][] Hashes, int ArenaBytes, double EncodeMBps)
    {
        public int Pages => Payloads.Length;
        public int RawFallbackPages => Raw.Count(r => r);
    }

    public sealed record Result(string Codec, int Pages, long RawBytes, long ArenaBytes, double RatioPercent,
        int RawFallbackPages, int IntegrityOk, int IntegrityBad, bool NegativeControlDetected,
        double DecodeP50Us, double DecodeP95Us, double EncodeMBps)
    {
        public bool Proven => IntegrityBad == 0 && IntegrityOk == Pages && NegativeControlDetected;
    }

    /// <summary>Сжать все страницы и посчитать честный размер арены (индекс + нагрузки).</summary>
    public static Packed Compress(IReadOnlyList<byte[]> pages, IPageCodec codec)
    {
        var payloads = new byte[pages.Count][];
        var raw = new bool[pages.Count];
        var hashes = new byte[pages.Count][];
        long arena = (long)pages.Count * IndexEntryBytes;
        long t = Stopwatch.GetTimestamp();
        for (int i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            if (p.Length != PageBytes) throw new ArgumentException("4096-byte pages required");
            hashes[i] = SHA256.HashData(p);
            var e = codec.Encode(p);
            if (e.Length >= PageBytes) { payloads[i] = (byte[])p.Clone(); raw[i] = true; }
            else payloads[i] = e;
            arena += payloads[i].Length;
        }
        double sec = (Stopwatch.GetTimestamp() - t) / (double)Stopwatch.Frequency;
        if (arena > int.MaxValue) throw new InvalidOperationException("Арена слишком большая для одного замера");
        double mbps = (long)pages.Count * PageBytes / 1048576.0 / Math.Max(1e-9, sec);
        return new Packed(payloads, raw, hashes, (int)arena, mbps);
    }

    /// <summary>Записать индекс и нагрузки в арену (управляемую или нативную).</summary>
    public static void WriteArena(Span<byte> arena, Packed packed)
    {
        if (arena.Length < packed.ArenaBytes) throw new ArgumentException("Арена меньше требуемого размера");
        int n = packed.Pages;
        int offset = n * IndexEntryBytes;
        for (int i = 0; i < n; i++)
        {
            var pl = packed.Payloads[i];
            BinaryPrimitives.WriteInt32LittleEndian(arena.Slice(i * IndexEntryBytes, 4), offset);
            BinaryPrimitives.WriteInt32LittleEndian(arena.Slice(i * IndexEntryBytes + 4, 4), pl.Length | (packed.Raw[i] ? RawFlag : 0));
            pl.CopyTo(arena.Slice(offset));
            offset += pl.Length;
        }
    }

    static bool RestoreOne(ReadOnlySpan<byte> arena, int i, byte[] expectedHash, IPageCodec codec, out double decodeUs)
    {
        decodeUs = 0;
        try
        {
            int off = BinaryPrimitives.ReadInt32LittleEndian(arena.Slice(i * IndexEntryBytes, 4));
            int lenRaw = BinaryPrimitives.ReadInt32LittleEndian(arena.Slice(i * IndexEntryBytes + 4, 4));
            bool isRaw = (lenRaw & RawFlag) != 0;
            int len = lenRaw & 0x7FFFFFFF;
            if (off < 0 || len < 0 || off + len > arena.Length) return false;
            var payload = arena.Slice(off, len).ToArray(); // копия вне замера времени
            long t = Stopwatch.GetTimestamp();
            byte[] page = isRaw ? payload : codec.Decode(payload, PageBytes);
            decodeUs = (Stopwatch.GetTimestamp() - t) * 1e6 / Stopwatch.Frequency;
            if (page.Length != PageBytes) return false;
            return SHA256.HashData(page).AsSpan().SequenceEqual(expectedHash);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            return false; // повреждённые данные: распаковщик отказался — это тоже «не совпало»
        }
    }

    /// <summary>Восстановить ВСЕ страницы из арены и сверить SHA-256 с исходными.</summary>
    public static (int Ok, int Bad, List<double> DecodeUs) Verify(ReadOnlySpan<byte> arena, Packed packed, IPageCodec codec)
    {
        int ok = 0, bad = 0; var lat = new List<double>(packed.Pages);
        for (int i = 0; i < packed.Pages; i++)
        {
            if (RestoreOne(arena, i, packed.Hashes[i], codec, out double us)) { ok++; lat.Add(us); } else bad++;
        }
        return (ok, bad, lat);
    }

    /// <summary>Отрицательный контроль: испортить один байт в копии арены — проверка обязана это поймать.</summary>
    public static bool NegativeControl(ReadOnlySpan<byte> arena, Packed packed, IPageCodec codec)
    {
        if (packed.Pages == 0) return false;
        // Страница с самой длинной сжатой нагрузкой (если все сырые — первая).
        int j = 0; int best = -1;
        for (int i = 0; i < packed.Pages; i++) if (!packed.Raw[i] && packed.Payloads[i].Length > best) { best = packed.Payloads[i].Length; j = i; }
        var copy = arena.Slice(0, packed.ArenaBytes).ToArray();
        int off = BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(j * IndexEntryBytes, 4));
        int len = BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(j * IndexEntryBytes + 4, 4)) & 0x7FFFFFFF;
        copy[off + len / 2] ^= 0xFF;
        return !RestoreOne(copy, j, packed.Hashes[j], codec, out _);
    }

    /// <summary>Полный прогон в управляемой памяти (портативно; для тестов и Linux).</summary>
    public static Result Run(IReadOnlyList<byte[]> pages, IPageCodec codec)
    {
        var packed = Compress(pages, codec);
        var arena = new byte[packed.ArenaBytes];
        WriteArena(arena, packed);
        var (ok, bad, lat) = Verify(arena, packed, codec);
        bool neg = NegativeControl(arena, packed, codec);
        return Summarize(codec.Name, packed, ok, bad, lat, neg);
    }

    public static Result Summarize(string codec, Packed packed, int ok, int bad, List<double> lat, bool negativeDetected)
    {
        long raw = (long)packed.Pages * PageBytes;
        lat.Sort();
        double Q(double q) => lat.Count == 0 ? 0 : lat[Math.Min(lat.Count - 1, (int)(q * lat.Count))];
        return new Result(codec, packed.Pages, raw, packed.ArenaBytes, raw == 0 ? 0 : 100.0 * packed.ArenaBytes / raw,
            packed.RawFallbackPages, ok, bad, negativeDetected, Q(0.5), Q(0.95), packed.EncodeMBps);
    }

    public static bool IsZero(byte[] p) { foreach (var b in p) if (b != 0) return false; return true; }
}
