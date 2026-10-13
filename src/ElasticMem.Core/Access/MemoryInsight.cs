using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace ElasticMem.Core.Access;

/// <summary>
/// Единый анализ памяти процесса (только чтение) под все три подхода сразу:
///  • аппаратное сжатие кеш-линий 64 Б (база + смещения, BΔI);
///  • адаптивные микроблоки (ElasticStore);
///  • постраничное сжатие 4 КБ (как в ОС);
///  • связи между живыми буферами (дубликаты линий, повторы значений, расширение int32→int64).
/// Данные подаются потоком (Feed) по регионам; итог — Report(). Память детектора связей ограничена лимитом.
/// </summary>
public sealed class MemoryInsight
{
    // --- BΔI конфигурации: (k байт элемента, d байт смещения) ---
    private static readonly (int k, int d)[] Bdi = { (8, 1), (4, 1), (8, 2), (2, 1), (4, 2), (8, 4) };
    private static int BdiSize(int k, int d) { int n = 64 / k; return k + (n + 7) / 8 + n * d; }

    private readonly long _relationBudgetBytes;
    private long _bytes, _lines, _ntLines;
    private long _bdiSize, _pageComp, _microSize, _pageCount, _microCount;
    private long _zeroLines, _repLines, _bdiPacked;
    // связи
    private readonly Dictionary<ulong, int> _lineHash;   // отпечаток нетривиальных линий 64 Б
    private readonly Dictionary<ulong, int> _wordHash;   // нетривиальные 8-байтовые слова
    private readonly HashSet<uint> _u32;                 // уникальные «настоящие» uint32 (обе половины слова ≠ мелочь)
    private long _relBytes;
    private bool _relFull;
    private readonly byte[] _page = new byte[4096];
    private int _pageFill;

    public MemoryInsight(long relationBudgetBytes = 256L << 20)
    {
        _relationBudgetBytes = relationBudgetBytes;
        _lineHash = new Dictionary<ulong, int>(1 << 20);
        _wordHash = new Dictionary<ulong, int>(1 << 20);
        _u32 = new HashSet<uint>(1 << 20);
    }

    private static ulong Mix(ulong h, ulong v) { h ^= v; h *= 0x9E3779B97F4A7C15UL; return h ^ (h >> 29); }

    private static bool FitsSigned(ulong x, int kb, int db)
    {
        ulong top = x >> (db - 1);
        ulong all = (1UL << (kb - db + 1)) - 1;
        return top == 0 || top == all;
    }

    private static int LineBdiSize(ReadOnlySpan<byte> line, out bool zero, out bool rep, out bool packed)
    {
        zero = rep = packed = false;
        var w = MemoryMarshal.Cast<byte, ulong>(line);
        bool allZero = true; for (int i = 0; i < 8; i++) if (w[i] != 0) { allZero = false; break; }
        if (allZero) { zero = true; return 0; }
        bool allEq = true; for (int i = 1; i < 8; i++) if (w[i] != w[0]) { allEq = false; break; }
        if (allEq) { rep = true; return 8; }
        foreach (var (k, d) in Bdi)
        {
            int kb = 8 * k, db = 8 * d, n = 64 / k;
            ulong baseV = BinaryPrimitives.ReadUInt64LittleEndian(line) & ((kb == 64) ? ulong.MaxValue : (1UL << kb) - 1);
            bool ok = true;
            for (int i = 0; i < n; i++)
            {
                ulong x = ReadElem(line, i, k);
                ulong dz = x, dd = (x - baseV) & ((kb == 64) ? ulong.MaxValue : (1UL << kb) - 1);
                if (!FitsSigned(dz, kb, db) && !FitsSigned(dd, kb, db)) { ok = false; break; }
            }
            if (ok) { packed = true; return BdiSize(k, d); }
        }
        return 64;
    }

    private static ulong ReadElem(ReadOnlySpan<byte> line, int i, int k) => k switch
    {
        8 => BinaryPrimitives.ReadUInt64LittleEndian(line[(i * 8)..]),
        4 => BinaryPrimitives.ReadUInt32LittleEndian(line[(i * 4)..]),
        2 => BinaryPrimitives.ReadUInt16LittleEndian(line[(i * 2)..]),
        _ => line[i],
    };

    /// <summary>Подать кусок памяти (может быть любой длины). Вызывать по регионам процесса.</summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        _bytes += data.Length;
        // страницы 4 КБ (Deflate) — с переносом хвоста
        while (data.Length > 0)
        {
            int take = Math.Min(4096 - _pageFill, data.Length);
            data[..take].CopyTo(_page.AsSpan(_pageFill));
            _pageFill += take; data = data[take..];
            if (_pageFill == 4096) { FlushPage(_page); _pageFill = 0; }
        }
    }

    private void FlushPage(ReadOnlySpan<byte> page)
    {
        _pageCount++;
        using (var ms = new MemoryStream(1024))
        {
            using (var z = new DeflateStream(ms, CompressionLevel.Fastest, true)) z.Write(page);
            _pageComp += Math.Min(4096, (int)ms.Length + 16);
        }
        // микроблоки 1024 Б
        for (int b = 0; b + 1024 <= 4096; b += 1024)
        {
            var cands = BlockCodec.Candidates(page.Slice(b, 1024), _microOpt);
            int best = 1024; foreach (var c in cands) if (c.SizeBytes < best) best = c.SizeBytes;
            _microSize += best + 16; _microCount++;
        }
        // линии 64 Б: BΔI + связи
        bool collectRel = !_relFull && _relBytes < _relationBudgetBytes;
        for (int o = 0; o < 4096; o += 64)
        {
            var line = page.Slice(o, 64);
            _lines++;
            int sz = LineBdiSize(line, out bool zero, out bool rep, out bool packed);
            _bdiSize += sz;
            if (zero) _zeroLines++; else if (rep) _repLines++; else if (packed) _bdiPacked++;
            bool triv = zero || rep;
            // нетривиальная линия: ≥4 слов > 255 и не все слова равны
            if (!triv)
            {
                var w = MemoryMarshal.Cast<byte, ulong>(line);
                int big = 0; for (int i = 0; i < 8; i++) if (w[i] > 255) big++;
                if (big >= 4) _ntLines++;
                if (collectRel && big >= 4)
                {
                    ulong h = 0; for (int i = 0; i < 8; i++) h = Mix(h, w[i]);
                    _lineHash[h] = _lineHash.GetValueOrDefault(h) + 1;
                    for (int i = 0; i < 8; i++)
                    {
                        ulong v = w[i];
                        if (v > 65535 && v != ulong.MaxValue) _wordHash[v] = _wordHash.GetValueOrDefault(v) + 1;
                        if (v >= (1UL << 32))
                        {
                            uint lo = (uint)v, hi = (uint)(v >> 32);
                            if (lo > 65535 && hi > 65535) { _u32.Add(lo); _u32.Add(hi); }
                        }
                    }
                }
            }
        }
        if (collectRel) { _relBytes += 4096; if (_relBytes >= _relationBudgetBytes) _relFull = true; }
    }

    private static readonly CodecOptions _microOpt = new();

    public sealed class InsightReport
    {
        public long ScannedBytes, RelationScannedBytes;
        public double BdiPct, PagePct, MicroPct;
        public double ZeroPct, RepPct, BdiPackedPct, RawPct;
        public double DupLinePct, RepeatedValuePct, PairValuePct, IntWidenPct;
        public bool RelationCapped;
    }

    public InsightReport Report()
    {
        if (_pageFill > 0) { _page.AsSpan(_pageFill).Clear(); FlushPage(_page); _pageFill = 0; }
        long dupLines = 0; foreach (var c in _lineHash.Values) if (c > 1) dupLines += c - 1;
        long extra = 0, pairs = 0;
        foreach (var kv in _wordHash) { if (kv.Value > 1) extra += kv.Value - 1; if (kv.Value == 2) pairs += 2; }
        long widen = 0;
        foreach (var kv in _wordHash) if (kv.Key < (1UL << 32) && _u32.Contains((uint)kv.Key)) widen += kv.Value;
        double relWords = Math.Max(1, _relBytes / 8.0);
        double sc = Math.Max(1, _bytes);
        return new InsightReport
        {
            ScannedBytes = _bytes, RelationScannedBytes = _relBytes, RelationCapped = _relFull,
            BdiPct = 100.0 * _bdiSize / sc,
            PagePct = 100.0 * _pageComp / Math.Max(1, _pageCount * 4096),
            MicroPct = 100.0 * _microSize / Math.Max(1, _microCount * 1024),
            ZeroPct = 100.0 * _zeroLines / Math.Max(1, _lines),
            RepPct = 100.0 * _repLines / Math.Max(1, _lines),
            BdiPackedPct = 100.0 * _bdiPacked / Math.Max(1, _lines),
            RawPct = 100.0 * (_lines - _zeroLines - _repLines - _bdiPacked) / Math.Max(1, _lines),
            DupLinePct = 100.0 * dupLines * 64 / Math.Max(1.0, _relBytes),
            RepeatedValuePct = 100.0 * extra * 8 / Math.Max(1.0, _relBytes),
            PairValuePct = 100.0 * pairs * 8 / Math.Max(1.0, _relBytes),
            IntWidenPct = 100.0 * widen * 8 / Math.Max(1.0, _relBytes),
        };
    }

    public static string Format(string title, InsightReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(title);
        sb.AppendLine($"Просканировано: {r.ScannedBytes / 1048576.0:F0} МБ (поиск связей — по первым {r.RelationScannedBytes / 1048576.0:F0} МБ{(r.RelationCapped ? ", достигнут лимит" : "")})");
        sb.AppendLine();
        sb.AppendLine("СЖАТИЕ (чем меньше %, тем лучше; % от исходного размера):");
        sb.AppendLine($"  Аппаратное BΔI, линии 64 Б (быстрое, произвольный доступ): {r.BdiPct,6:F1}%");
        sb.AppendLine($"  Адаптивные микроблоки 1 КБ (ElasticStore):                {r.MicroPct,6:F1}%");
        sb.AppendLine($"  Постраничное сжатие 4 КБ, Deflate (как в ОС):             {r.PagePct,6:F1}%");
        sb.AppendLine($"    Линии: нули {r.ZeroPct:F1}%, повтор {r.RepPct:F1}%, упакованы BΔI {r.BdiPackedPct:F1}%, несжимаемы {r.RawPct:F1}%");
        sb.AppendLine();
        sb.AppendLine("СВЯЗИ МЕЖДУ ЖИВЫМИ БУФЕРАМИ (доля памяти; это верхние границы, связь ≠ повтор значения):");
        sb.AppendLine($"  Точные дубликаты линий 64 Б:                       {r.DupLinePct,6:F2}%");
        sb.AppendLine($"  Значения-повторы (граница для перестановок):       {r.RepeatedValuePct,6:F2}%");
        sb.AppendLine($"    из них «ровно 2 копии» (похоже на копию буфера): {r.PairValuePct,6:F2}%");
        sb.AppendLine($"  Расширение int32→int64 (граница):                  {r.IntWidenPct,6:F2}%");
        sb.AppendLine();
        string verdict = r.BdiPct < 70 ? "хорошо сжимается аппаратно"
            : r.MicroPct < 50 ? "сжимается микроблоками, аппаратно — слабо"
            : "плохо сжимается без потерь (вероятно, уже сжатые данные: медиа, JIT-код, шифр)";
        string rel = r.PairValuePct > 5 ? "есть заметные парные копии — связи возможны"
            : "простых связей между буферами почти нет";
        sb.AppendLine($"Итог: {verdict}; {rel}.");
        return sb.ToString();
    }
}
