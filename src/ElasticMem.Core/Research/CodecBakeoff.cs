using System.Diagnostics;
using System.Security.Cryptography;
using ZstdSharp;
namespace ElasticMem.Core.Research;

/// <summary>
/// Сравнение кодеков на КОПИЯХ реальных страниц приложения. Ничего не пишет на диск и никуда не отправляет:
/// принимает страницы в памяти, возвращает только статистику. Главный вопрос — может ли своё хранилище сжимать
/// лучше штатного Windows (XPRESS по одной странице 4 КиБ без словаря). Наши рычаги, которых нет у Windows:
/// словарь, обученный на страницах того же приложения; блоки крупнее 4 КиБ; дедупликация одинаковых страниц;
/// арифметический BΔI-кодек (BlockCodec). Честность: словарь обучается на одной половине страниц, проверка — на другой.
/// </summary>
public static class CodecBakeoff
{
    public sealed record Row(string Codec, int Pages, long RawBytes, long StoredBytes, double RatioPercent,
        double DecodeP50Us, double DecodeP95Us, double EncodeMBps, string Note);
    public sealed record Report(int SampledPages, int ZeroPages, int TestPages, int DuplicatePages, long DuplicateBytes,
        Row[] Rows, string? Error);

    static double Q(List<double> v, double q) { if (v.Count == 0) return 0; v.Sort(); return v[Math.Min(v.Count - 1, (int)(q * v.Count))]; }
    static double Us(long t) => (Stopwatch.GetTimestamp() - t) * 1e6 / Stopwatch.Frequency;
    static bool IsZero(byte[] p) { foreach (var b in p) if (b != 0) return false; return true; }

    /// <param name="pages">Копии страниц по 4096 байт.</param>
    /// <param name="windowsCodecs">Добавить xpress/xpress-huffman (только Windows).</param>
    public static Report Run(IReadOnlyList<byte[]> pages, bool windowsCodecs, Action<string>? progress = null, CancellationToken ct = default)
    {
        if (pages.Any(p => p.Length != 4096)) throw new ArgumentException("4096-byte pages required");
        int zero = pages.Count(IsZero);
        // Нулевые страницы Windows и так не хранит (demand-zero) — исключаем из сравнения сжатия.
        var nonZero = pages.Where(p => !IsZero(p)).ToList();
        // Детерминированно перемешиваем и делим: первая половина — обучение словаря, вторая — проверка.
        var rng = new Random(20261024);
        for (int i = nonZero.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (nonZero[i], nonZero[j]) = (nonZero[j], nonZero[i]); }
        int half = nonZero.Count / 2;
        var train = nonZero.Take(Math.Min(half, 2048)).ToList();
        var test = nonZero.Skip(half).Take(4096).ToList();
        if (test.Count < 16) return new(pages.Count, zero, test.Count, 0, 0, Array.Empty<Row>(), "Слишком мало ненулевых страниц для сравнения");

        // Дедупликация на проверочной половине: точные копии (хеш + сравнение байтов).
        var seen = new Dictionary<string, byte[]>(); int dup = 0;
        foreach (var p in test)
        {
            string h = Convert.ToHexString(SHA256.HashData(p));
            if (seen.TryGetValue(h, out var q) && q.AsSpan().SequenceEqual(p)) dup++; else seen[h] = p;
        }

        var rows = new List<Row>();
        long raw = (long)test.Count * 4096;

        Row Measure(string name, IPageCodec codec, int blockPages, string note)
        {
            ct.ThrowIfCancellationRequested(); progress?.Invoke(name);
            for (int w = 0; w < 8; w++) { var e = codec.Encode(test[w % test.Count]); _ = codec.Decode(e, 4096); } // прогрев
            long stored = 0; var dec = new List<double>(); double encUs = 0; int bytesIn = 0;
            for (int i = 0; i < test.Count; i += blockPages)
            {
                int n = Math.Min(blockPages, test.Count - i);
                var block = new byte[n * 4096];
                for (int k = 0; k < n; k++) test[i + k].CopyTo(block, k * 4096);
                long t = Stopwatch.GetTimestamp(); var packed = codec.Encode(block); encUs += Us(t); bytesIn += block.Length;
                bool fallback = packed.Length >= block.Length; // невыгодно — храним как есть, как сделало бы любое хранилище
                stored += fallback ? block.Length : packed.Length;
                t = Stopwatch.GetTimestamp(); var back = fallback ? (byte[])block.Clone() : codec.Decode(packed, block.Length); double us = Us(t);
                if (!back.AsSpan().SequenceEqual(block)) throw new InvalidDataException(name + ": данные не совпали после распаковки");
                dec.Add(us / n); // время распаковки в пересчёте на одну страницу 4 КиБ
            }
            return new(name, test.Count, raw, stored, 100.0 * stored / raw, Q(dec, 0.5), Q(dec, 0.95), bytesIn / Math.Max(1e-9, encUs), note);
        }

        try
        {
            if (windowsCodecs && OperatingSystem.IsWindows())
            {
                using (var x = new WindowsXpressCodec(false)) rows.Add(Measure("xpress 4K", x, 1, "как сжимает Windows Memory Compression"));
                using (var x = new WindowsXpressCodec(true)) rows.Add(Measure("xpress-huffman 4K", x, 1, "тот же Windows API, сильнее"));
            }
            using (var c = new PortablePageCodec("lz4")) rows.Add(Measure("lz4 4K", c, 1, "самый быстрый"));
            using (var c = new PortablePageCodec("zstd1")) rows.Add(Measure("zstd-1 4K", c, 1, "без словаря"));
            byte[]? dict = null; string? dictErr = null;
            try { dict = DictBuilder.TrainFromBuffer(train, 65536); } catch (Exception ex) { dictErr = ex.GetType().Name; }
            if (dict != null) using (var c = new PortablePageCodec("zstd1", dict)) rows.Add(Measure("zstd-1 4K + словарь", c, 1, "словарь из страниц этого же приложения — у Windows такого нет"));
            else rows.Add(new("zstd-1 4K + словарь", 0, raw, raw, 100, 0, 0, 0, "словарь не обучился: " + dictErr));
            using (var c = new PortablePageCodec("zstd1")) rows.Add(Measure("zstd-1 16K блок", c, 4, "4 страницы вместе; распаковка отдаёт сразу 4"));
            rows.Add(MeasureBdi(test, raw, ct));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new(pages.Count, zero, test.Count, dup, (long)dup * 4096, rows.ToArray(), ex.GetType().Name + ": " + ex.Message); }

        return new(pages.Count, zero, test.Count, dup, (long)dup * 4096, rows.ToArray(), null);
    }

    // Наш арифметический кодек BΔI: страница делится на микроблоки по 256 Б, для каждого — лучший из
    // Zero/BitPack/BaseDelta/Sparse/Raw (без LZ). Плюс: часть значений можно читать без распаковки блока.
    static Row MeasureBdi(List<byte[]> test, long raw, CancellationToken ct)
    {
        var opt = new CodecOptions { EnableLz = false };
        long stored = 0; var dec = new List<double>(); double encUs = 0;
        var dst = new byte[256];
        foreach (var p in test)
        {
            ct.ThrowIfCancellationRequested();
            var blocks = new EncodedBlock[16];
            long t = Stopwatch.GetTimestamp();
            for (int b = 0; b < 16; b++)
            {
                var span = p.AsSpan(b * 256, 256);
                var best = BlockCodec.Candidates(span, opt).MinBy(c => c.SizeBytes)!;
                blocks[b] = BlockCodec.Build(span, best, opt);
            }
            encUs += Us(t);
            int size = blocks.Sum(x => x.SizeBytes);
            stored += Math.Min(size, 4096);
            t = Stopwatch.GetTimestamp();
            for (int b = 0; b < 16; b++)
            {
                BlockCodec.Decode(blocks[b], dst);
                if (!dst.AsSpan().SequenceEqual(p.AsSpan(b * 256, 256))) throw new InvalidDataException("BΔI: данные не совпали");
            }
            dec.Add(Us(t));
        }
        return new("BΔI 256 Б (наш)", test.Count, raw, stored, 100.0 * stored / raw, Q(dec, 0.5), Q(dec, 0.95), raw / Math.Max(1e-9, encUs), "арифметический, частичное чтение без распаковки");
    }
}
