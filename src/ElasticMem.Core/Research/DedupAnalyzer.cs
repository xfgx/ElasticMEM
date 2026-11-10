using System.IO.Compression;

namespace ElasticMem.Core.Research;

/// <summary>
/// Измерение потолка дедупликации на наборе 4-КиБ страниц (можно собранных со многих процессов сразу).
/// Полноценная реализация поверх <see cref="DedupStore"/>: чанки реально складываются в хранилище с
/// дедупом и сжатием, а не оцениваются приблизительно. Сравнивается «дедуп → сжатие» против обычного
/// per-page сжатия (Deflate), которое по своей природе не видит повторов между страницами и процессами.
/// </summary>
public static class DedupAnalyzer
{
    public const int PageBytes = 4096;

    public sealed record Result(
        string Chunking, int ChunkBytes,
        long TotalChunks, long UniqueChunks, double UniqueRatio,
        long PerPageCompressedBytes, long DedupThenCompressedBytes, double SavingVsPerPagePct)
    {
        public override string ToString() =>
            $"{Chunking,-14} чанк≈{ChunkBytes,5}Б  уник {UniqueRatio * 100,6:0.00}%  " +
            $"per-page {PerPageCompressedBytes / 1048576.0,7:0.00} МиБ → дедуп+сжат {DedupThenCompressedBytes / 1048576.0,7:0.00} МиБ  " +
            $"экономия {SavingVsPerPagePct,6:0.00}%";
    }

    public sealed record Report(int Pages, long RawBytes, long WholePageDuplicateBytes,
        long ZeroPageBytes, long NonZeroDuplicateBytes, List<Result> Results);

    public static Report Measure(IReadOnlyList<byte[]> pages)
    {
        long raw = (long)pages.Count * PageBytes;
        long perPage = 0;
        var seenPages = new HashSet<string>();
        var seenNonZero = new HashSet<string>();
        long wholeDup = 0, zeroBytes = 0, nonZeroDup = 0;
        foreach (var p in pages)
        {
            perPage += Deflate(p);
            bool zero = true; foreach (byte b in p) if (b != 0) { zero = false; break; }
            string k = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(p));
            if (!seenPages.Add(k)) wholeDup += PageBytes;
            if (zero) { zeroBytes += PageBytes; continue; } // Windows уже разделяет нулевую страницу — исключаем
            if (!seenNonZero.Add(k)) nonZeroDup += PageBytes; // одинаковая НЕнулевая приватная страница между процессами
        }

        var results = new List<Result>
        {
            FixedChunk(pages, 4096, perPage),
            FixedChunk(pages, 1024, perPage),
            FixedChunk(pages, 512, perPage),
            FixedChunk(pages, 64, perPage),
            ContentDefined(pages, perPage),
        };
        return new Report(pages.Count, raw, wholeDup, zeroBytes, nonZeroDup, results);
    }

    static Result FixedChunk(IReadOnlyList<byte[]> pages, int chunk, long perPage)
    {
        var store = new DedupStore();
        long total = 0;
        foreach (var page in pages)
            for (int off = 0; off < page.Length; off += chunk)
            {
                int len = Math.Min(chunk, page.Length - off);
                store.Put(page.AsSpan(off, len));
                total++;
            }
        return Make($"fixed-{chunk}", chunk, total, store, perPage);
    }

    // Content-defined chunking: Rabin-подобный rolling hash, средний размер ≈2^maskBits байт.
    static Result ContentDefined(IReadOnlyList<byte[]> pages, long perPage)
    {
        const int window = 48, min = 128, max = 2048, maskBits = 9; // ≈512 Б
        ulong mask = (1UL << maskBits) - 1;
        var store = new DedupStore();
        long total = 0, sumLen = 0, count = 0;
        foreach (var page in pages)
        {
            int start = 0; ulong roll = 0;
            for (int i = 0; i < page.Length; i++)
            {
                roll = (roll << 1) + page[i];
                if (i - start >= window) roll -= (ulong)page[i - window] << window;
                int len = i - start + 1;
                if ((len >= min && (roll & mask) == mask) || len >= max || i == page.Length - 1)
                {
                    store.Put(page.AsSpan(start, len));
                    total++; sumLen += len; count++;
                    start = i + 1; roll = 0;
                }
            }
        }
        return Make("cdc~512", count > 0 ? (int)(sumLen / count) : 0, total, store, perPage);
    }

    static Result Make(string name, int chunk, long total, DedupStore store, long perPage)
    {
        double uniqueRatio = total == 0 ? 0 : (double)store.UniqueChunks / total;
        double saving = perPage == 0 ? 0 : (1.0 - (double)store.StoredBytes / perPage) * 100.0;
        return new Result(name, chunk, total, store.UniqueChunks, uniqueRatio, perPage, store.StoredBytes, saving);
    }

    static long Deflate(byte[] data)
    {
        using var ms = new MemoryStream(data.Length);
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(data, 0, data.Length);
        return ms.Length;
    }

    public static string Format(Report r)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Страниц: {r.Pages} ({r.RawBytes / 1048576.0:0.0} МиБ). Полных дубликатов страниц: {r.WholePageDuplicateBytes / 1048576.0:0.0} МиБ.");
        sb.AppendLine();
        sb.AppendLine("── Модель A: дедуп как добавка к сжатию холодного tier ──");
        sb.AppendLine("Гранулярность     | уникальных | per-page сжатие → дедуп+сжатие | экономия сверх per-page");
        foreach (var x in r.Results) sb.AppendLine("  " + x);
        var best = r.Results.MaxBy(x => x.SavingVsPerPagePct)!;
        sb.AppendLine($"Лучшее: {best.Chunking} — дедуп убирает {best.SavingVsPerPagePct:0.0}% сверх per-page сжатия.");
        sb.AppendLine(best.SavingVsPerPagePct >= 15
            ? "  → потолок есть: дедуп-store поверх сжатия оправдан."
            : "  → низкий потолок: дубликаты и так дёшевы для сжатия, дедуп поверх сжатия смысла почти не даёт.");

        // Модель B: разделение одинаковых физических страниц (KSM/CoW) — без сжатия, на ГОРЯЧЕЙ памяти.
        double nonZeroPct = r.RawBytes > 0 ? (double)r.NonZeroDuplicateBytes / r.RawBytes * 100 : 0;
        sb.AppendLine();
        sb.AppendLine("── Модель B: разделение одинаковых страниц (KSM/CoW), без сжатия, горячая RAM ──");
        sb.AppendLine($"Нулевых страниц: {r.ZeroPageBytes / 1048576.0:0.0} МиБ — Windows их уже разделяет, не считаем.");
        sb.AppendLine($"Одинаковых НЕнулевых приватных страниц между процессами: {r.NonZeroDuplicateBytes / 1048576.0:0.0} МиБ = {nonZeroPct:0.0}% собранного.");
        sb.AppendLine($"  → столько физической RAM вернуло бы CoW-разделение страниц — без распаковки, на горячей памяти.");
        sb.AppendLine(nonZeroPct >= 15
            ? "  → ВЫСОКИЙ потолок page-sharing: CoW-разделение (аналог KSM) стоит оценивать всерьёз — это то, чего Windows не делает."
            : nonZeroPct >= 7
                ? "  → умеренный потолок page-sharing: возможно оправдано, нужно учесть стоимость сканирования/хеширования."
                : "  → низкий потолок page-sharing на этих данных: одинаковых ненулевых приватных страниц мало.");
        return sb.ToString();
    }

    /// <summary>Самопроверка: синтетика с ~60% дубликатов между «процессами» + проверка Get/Release хранилища.</summary>
    public static string SelfTest()
    {
        var rnd = new Random(42);
        var baseline = new List<byte[]>();
        for (int i = 0; i < 200; i++) { var p = new byte[PageBytes]; rnd.NextBytes(p); baseline.Add(p); }
        var pages = new List<byte[]>(baseline);
        for (int proc = 0; proc < 2; proc++)
            for (int i = 0; i < 200; i++)
                pages.Add(rnd.NextDouble() < 0.6 ? (byte[])baseline[i].Clone() : RandomPage(rnd));

        // 1. store roundtrip + refcount
        var store = new DedupStore();
        var p0 = baseline[0];
        long id1 = store.Put(p0), id2 = store.Put((byte[])p0.Clone());
        bool roundtrip = store.Get(id1).AsSpan().SequenceEqual(p0);
        bool deduped = id1 == id2 && store.RefCount(id1) == 2 && store.UniqueChunks == 1;
        store.Release(id1); bool heldAfterOneRelease = store.RefCount(id2) == 1;
        store.Release(id2); bool freed = store.UniqueChunks == 0;

        // 2. ceiling
        var rep = Measure(pages);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("DEDUP self-test:");
        sb.AppendLine($"  store: roundtrip={(roundtrip ? "ok" : "FAIL")}, dedup+refcount={(deduped ? "ok" : "FAIL")}, release={(heldAfterOneRelease && freed ? "ok" : "FAIL")}");
        sb.Append(Format(rep));
        var page4k = rep.Results[0];
        sb.AppendLine(roundtrip && deduped && heldAfterOneRelease && freed && page4k.SavingVsPerPagePct > 10
            ? "  OK: хранилище корректно и дедуп реально сокращает на дублирующихся страницах."
            : "  ВНИМАНИЕ: проверьте реализацию.");
        return sb.ToString();
    }

    static byte[] RandomPage(Random rnd) { var p = new byte[PageBytes]; rnd.NextBytes(p); return p; }
}
