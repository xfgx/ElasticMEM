using System.IO.Compression;

namespace ElasticMem.Core.Research;

/// <summary>
/// Ceiling-эксперимент для идеи «content-addressed cold tier» (см. docs/dedup-cold-tier.md).
///
/// Чистая, тестируемая логика без вызовов Windows: на вход — копии 4-КиБ страниц (можно со многих
/// процессов сразу), на выход — сколько даёт ДЕДУПЛИКАЦИЯ между страницами/процессами поверх обычного
/// per-page сжатия. Именно эту ось не трогают ни Windows Memory Compression, ни прежние механизмы проекта.
///
/// Сжатие здесь — Deflate как переносимый прокси (на Windows замеряется настоящим XPRESS). Хеш чанка —
/// FNV-1a/64: для оценки потолка коллизии пренебрежимы; для реального хранилища нужен крипто-хеш.
/// </summary>
public static class DedupCeiling
{
    public const int PageBytes = 4096;

    public sealed record Result(
        string Chunking, int ChunkBytes,
        long TotalChunks, long UniqueChunks, double UniqueRatio,
        long RawBytes, long PerPageCompressedBytes, long DedupThenCompressedBytes,
        double SavingVsPerPagePct)
    {
        public override string ToString() =>
            $"{Chunking,-16} чанк≈{ChunkBytes,5}Б  уник {UniqueRatio * 100,6:0.00}%  " +
            $"per-page {PerPageCompressedBytes / 1024.0 / 1024.0,7:0.00} МиБ → дедуп+сжат {DedupThenCompressedBytes / 1024.0 / 1024.0,7:0.00} МиБ  " +
            $"экономия сверх per-page {SavingVsPerPagePct,6:0.00}%";
    }

    /// <summary>Посчитать потолок для набора страниц разными гранулярностями чанков.</summary>
    public static List<Result> Measure(IReadOnlyList<byte[]> pages)
    {
        long raw = (long)pages.Count * PageBytes;
        long perPage = 0;
        foreach (var p in pages) perPage += Deflate(p);

        var results = new List<Result>
        {
            Fixed(pages, 4096, raw, perPage),
            Fixed(pages, 512, raw, perPage),
            Fixed(pages, 64, raw, perPage),
            ContentDefined(pages, raw, perPage),
        };
        return results;
    }

    static Result Fixed(IReadOnlyList<byte[]> pages, int chunk, long raw, long perPage)
    {
        var unique = new Dictionary<ulong, byte[]>();
        long total = 0;
        foreach (var page in pages)
            for (int off = 0; off < page.Length; off += chunk)
            {
                int len = Math.Min(chunk, page.Length - off);
                total++;
                var span = page.AsSpan(off, len);
                ulong h = Fnv(span);
                if (!unique.ContainsKey(h)) unique[h] = span.ToArray();
            }
        return Finish($"fixed-{chunk}", chunk, total, unique, raw, perPage);
    }

    // Rabin-подобный rolling hash: граница чанка, когда младшие биты хеша окна == маске.
    // Средний размер ≈ 2^maskBits байт, с ограничением min/max.
    static Result ContentDefined(IReadOnlyList<byte[]> pages, long raw, long perPage)
    {
        const int window = 48, min = 128, max = 2048, maskBits = 9; // ≈512 Б в среднем
        ulong mask = (1UL << maskBits) - 1;
        var unique = new Dictionary<ulong, byte[]>();
        long total = 0, sumLen = 0, count = 0;
        foreach (var page in pages)
        {
            int start = 0; ulong roll = 0;
            for (int i = 0; i < page.Length; i++)
            {
                roll = (roll << 1) + page[i];
                if (i - start >= window) roll -= (ulong)page[i - window] << window;
                int len = i - start + 1;
                bool boundary = len >= min && ((roll & mask) == mask || len >= max);
                if (boundary || i == page.Length - 1)
                {
                    var span = page.AsSpan(start, len);
                    total++; sumLen += len; count++;
                    ulong h = Fnv(span);
                    if (!unique.ContainsKey(h)) unique[h] = span.ToArray();
                    start = i + 1; roll = 0;
                }
            }
        }
        int avg = count > 0 ? (int)(sumLen / count) : 0;
        return Finish("cdc~512", avg, total, unique, raw, perPage);
    }

    static Result Finish(string name, int chunk, long total, Dictionary<ulong, byte[]> unique, long raw, long perPage)
    {
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true))
            foreach (var c in unique.Values) ds.Write(c, 0, c.Length);
        long dedupThenCompressed = ms.Length;
        double uniqueRatio = total == 0 ? 0 : (double)unique.Count / total;
        double saving = perPage == 0 ? 0 : (1.0 - (double)dedupThenCompressed / perPage) * 100.0;
        return new Result(name, chunk, total, unique.Count, uniqueRatio, raw, perPage, dedupThenCompressed, saving);
    }

    static long Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(data, 0, data.Length);
        return ms.Length;
    }

    static ulong Fnv(ReadOnlySpan<byte> data)
    {
        ulong h = 1469598103934665603UL;
        foreach (byte b in data) { h ^= b; h *= 1099511628211UL; }
        return h;
    }

    /// <summary>Самопроверка на синтетике с заданной долей дубликатов между «процессами».</summary>
    public static string SelfTest()
    {
        var rnd = new Random(42);
        var pages = new List<byte[]>();
        // "Процесс" из 200 уникальных страниц; ещё два процесса на 60% повторяют первый (эмуляция renderer'ов).
        var baseline = new List<byte[]>();
        for (int i = 0; i < 200; i++) { var p = new byte[PageBytes]; rnd.NextBytes(p); baseline.Add(p); }
        pages.AddRange(baseline);
        for (int proc = 0; proc < 2; proc++)
            for (int i = 0; i < 200; i++)
                pages.Add(rnd.NextDouble() < 0.6 ? (byte[])baseline[i].Clone() : RandomPage(rnd));

        var results = Measure(pages);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"DEDUP CEILING self-test: {pages.Count} страниц (3 «процесса», ~60% дубликатов между ними)");
        foreach (var r in results) sb.AppendLine("  " + r);
        var page4k = results[0];
        bool ok = page4k.UniqueRatio < 0.95 && page4k.SavingVsPerPagePct > 10;
        sb.AppendLine(ok ? "  OK: дедуп реально сокращает (как и ожидалось на дублирующихся страницах)"
                         : "  ВНИМАНИЕ: дедуп не сократил — проверьте логику");
        return sb.ToString();
    }

    static byte[] RandomPage(Random rnd) { var p = new byte[PageBytes]; rnd.NextBytes(p); return p; }
}
