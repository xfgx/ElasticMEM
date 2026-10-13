using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace ElasticMem.Core.Access;

public interface IArray64
{
    string Name { get; }
    ulong Get(long i);
    void Set(long i, ulong v);
    ulong Sum(long start, long count);
    void Maintain();
    long Footprint { get; }
    string Describe();
}

public sealed class RawArray64 : IArray64
{
    private readonly ulong[] _a;
    public RawArray64(long n) => _a = new ulong[n];
    public string Name => "Обычный массив (без сжатия)";
    public ulong Get(long i) => _a[i];
    public void Set(long i, ulong v) => _a[i] = v;
    public ulong Sum(long s, long c) => Kernels.SumU64(_a.AsSpan((int)s, (int)c));
    public void Maintain() { }
    public long Footprint => _a.LongLength * 8;
    public string Describe() => "";
}

public sealed class ElasticAdapter : IArray64
{
    public readonly ElasticArray64 A;
    public ElasticAdapter(ElasticArray64 a) => A = a;
    public string Name => A.Name;
    public ulong Get(long i) => A.Get(i);
    public void Set(long i, ulong v) => A.Set(i, v);
    public ulong Sum(long s, long c) => A.Sum(s, c);
    public void Maintain() => A.Maintain();
    public long Footprint => A.Footprint;
    public string Describe()
    {
        var s = A.Stats;
        return "чанки: " + string.Join(", ", Enum.GetValues<Rep>().Where(r => s.ChunksByRep[(int)r] > 0).Select(r => $"{r}={s.ChunksByRep[(int)r]}")) +
               $"; в основу {s.InPlaceWrites}, в слой изменений {s.OverlayWrites}, горячих строк {s.HotLinePromotions}, " +
               $"возвратов к обычному {s.RawPromotions}, слияний {s.Merges}, распаковок Deflate {s.DenseDecodes}" + (s.BudgetMet ? "" : "; БЮДЖЕТ НЕДОСТИЖИМ");
    }
}

/// <summary>Модель прозрачного постраничного сжатия (как в ОС): обращение к сжатой странице → распаковка всей страницы.</summary>
public sealed class PagedArray64 : IArray64
{
    private const int PW = 512;
    private readonly ulong[]?[] _raw; private readonly byte[]?[] _lz; private readonly int[] _last;
    private int _epoch; private readonly long _budget; private long _faults;
    public PagedArray64(long n, long budget)
    {
        int p = (int)((n + PW - 1) / PW);
        _raw = new ulong[p][]; _lz = new byte[p][]; _last = new int[p];
        for (int i = 0; i < p; i++) _raw[i] = new ulong[PW];
        _budget = budget;
    }
    public string Name => $"Постраничное сжатие 4 КБ (как в ОС), бюджет {(_budget == long.MaxValue ? "∞" : (_budget >> 20) + " МБ")}";
    private ulong[] Page(long i)
    {
        int p = (int)(i / PW);
        _last[p] = _epoch;
        var r = _raw[p];
        if (r != null) return r;
        r = new ulong[PW];
        using (var z = new DeflateStream(new MemoryStream(_lz[p]!), CompressionMode.Decompress))
        {
            var b = MemoryMarshal.AsBytes(r.AsSpan()); int got = 0;
            while (got < b.Length) got += z.Read(b[got..]);
        }
        _lz[p] = null; _raw[p] = r; _faults++;
        return r;
    }
    public ulong Get(long i) => Page(i)[i % PW];
    public void Set(long i, ulong v) => Page(i)[i % PW] = v;
    public ulong Sum(long s, long c)
    {
        ulong r = 0;
        for (long i = s; i < s + c;) { int len = (int)Math.Min(PW - i % PW, s + c - i); r += Kernels.SumU64(Page(i).AsSpan((int)(i % PW), len)); i += len; }
        return r;
    }
    public long Footprint { get { long f = _raw.Length * 16L; for (int i = 0; i < _raw.Length; i++) f += _raw[i] != null ? PW * 8 : _lz[i]!.Length; return f; } }
    public void Maintain()
    {
        _epoch++;
        if (Footprint <= _budget) return;
        // вытесняем (сжимаем) давно не использованные страницы, пока не уложимся в бюджет
        var order = Enumerable.Range(0, _raw.Length).Where(i => _raw[i] != null).OrderBy(i => _last[i]).ToList();
        long f = Footprint;
        foreach (var p in order)
        {
            if (f <= _budget) break;
            var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, CompressionLevel.Fastest, true)) z.Write(MemoryMarshal.AsBytes(_raw[p].AsSpan()));
            var lz = ms.ToArray();
            if (lz.Length >= PW * 8) continue;
            f -= PW * 8 - lz.Length;
            _lz[p] = lz; _raw[p] = null;
        }
    }
    public string Describe() => $"распаковок страниц: {_faults}";
}

public sealed class AccessBenchResult
{
    public string Name = "", Detail = "";
    public double OpsMs, MaintainMs, AvgFootprintMb, LogicalMb;
    public double[] ZoneMs = new double[6];
    public bool Verified;
}

/// <summary>
/// Шесть зон с разным поведением: последовательные чтения, редкие случайные чтения, горячий участок записей,
/// частые случайные записи по всей зоне, долго не используемые данные, разреженный массив.
/// </summary>
public static class AccessBench
{
    /// <summary>Чистый замер: последовательный проход и случайные чтения — обычный массив против сжатых представлений.</summary>
    public static string Micro(long n = 16 << 20)
    {
        var sb = new StringBuilder();
        var rnd = new Random(2);
        var plain = new ulong[n];
        for (long i = 0; i < n; i++) plain[i] = 1_000_000 + (ulong)rnd.Next(4096);
        var idx = new long[4_000_000];
        for (int i = 0; i < idx.Length; i++) idx[i] = rnd.NextInt64(n);
        sb.AppendLine($"Массив {n * 8 >> 20} МБ, значения 1 000 000 + [0..4095] (12 бит). Лучший из 3 замеров.");
        double Best(Func<double> f) { f(); return Enumerable.Range(0, 3).Min(_ => f()); }
        ulong ex = Kernels.SumU64(plain);
        double tp = Best(() => { var w = Stopwatch.StartNew(); Kernels.SumU64(plain); return w.Elapsed.TotalMilliseconds; });
        double rp = Best(() => { var w = Stopwatch.StartNew(); ulong x = 0; foreach (var i in idx) x += plain[i]; return w.Elapsed.TotalNanoseconds / idx.Length; });
        sb.AppendLine($"  {"Обычный массив",-34} {n * 8 >> 20,5} МБ   проход {tp,7:F1} мс   случайное чтение {rp,5:F1} нс");
        foreach (var rep in new[] { Rep.Aligned, Rep.Packed })
        {
            var a = new ElasticArray64(n);
            for (long i = 0; i < n; i++) a.Set(i, plain[i]);
            a.ForceAll(rep);
            bool ok = a.Sum(0, n) == ex;
            double ts = Best(() => { var w = Stopwatch.StartNew(); a.Sum(0, n); return w.Elapsed.TotalMilliseconds; });
            double rr = Best(() => { var w = Stopwatch.StartNew(); ulong x = 0; foreach (var i in idx) x += a.Get(i); return w.Elapsed.TotalNanoseconds / idx.Length; });
            string name = rep == Rep.Aligned ? "База + 16-битные смещения (вектор)" : "База + 12-битные смещения";
            sb.AppendLine($"  {name,-34} {a.Footprint >> 20,5} МБ   проход {ts,7:F1} мс ({tp / ts:F2}×)   случайное чтение {rr,5:F1} нс ({rp / rr:F2}×){(ok ? "" : "  ОШИБКА")}");
        }
        return sb.ToString();
    }

    public static List<AccessBenchResult> Run(long zoneWords = 2 << 20, int epochs = 16, Action<string>? log = null, CostModel? costs = null, double[]? budgets = null)
    {
        costs ??= Calibration.Measure(log: log);
        long n = zoneWords * 6, rawBytes = n * 8;
        budgets ??= new[] { 1.0, 0.5, 0.3 };
        var makers = new List<Func<IArray64>> { () => new RawArray64(n) };
        foreach (var b in budgets.Where(b => b < 1)) makers.Add(() => new PagedArray64(n, (long)(rawBytes * b)));
        makers.Add(() => new ElasticAdapter(new ElasticArray64(n, new ElasticArrayOptions { Name = "Микроблоки: выбор только по содержимому", AccessAware = false, Costs = costs })));
        foreach (var b in budgets)
            makers.Add(() => new ElasticAdapter(new ElasticArray64(n, new ElasticArrayOptions
            { Name = $"Микроблоки: выбор по способу доступа, бюджет {b * 100:F0}%", AccessAware = true, BudgetBytes = (long)(rawBytes * b), Costs = costs })));
        var res = new List<AccessBenchResult>();
        foreach (var mk in makers)
        {
            var a = mk();
            log?.Invoke("Запуск: " + a.Name);
            res.Add(RunOne(a, n, zoneWords, epochs));
            a = null; GC.Collect();
        }
        return res;
    }

    private static AccessBenchResult RunOne(IArray64 a, long n, long z, int epochs)
    {
        var rnd = new Random(11);
        var sh = new ulong[n];
        void Init(long i, ulong v) { a.Set(i, v); sh[i] = v; }
        long Z(int k) => k * z;
        ulong[] patterns = Enumerable.Range(0, 16).Select(i => 0x2020_4F4B_0000_0000UL + (ulong)i * 0x101).ToArray();
        for (long i = 0; i < z; i++)
        {
            Init(Z(0) + i, 1_000_000 + (ulong)rnd.Next(4096));
            Init(Z(1) + i, 5_000_000_000 + (ulong)rnd.Next(65536));
            Init(Z(2) + i, (ulong)rnd.Next(1024));
            Init(Z(3) + i, (ulong)rnd.NextInt64());
            Init(Z(4) + i, patterns[(i / 7 + rnd.Next(2)) % 16]);
            Init(Z(5) + i, rnd.Next(33) == 0 ? (ulong)rnd.NextInt64() : 0);
        }
        a.Maintain(); a.Maintain(); a.Maintain(); a.Maintain(); // зона 4 остывает

        var r = new AccessBenchResult { Name = a.Name, LogicalMb = n * 8 / 1048576.0, Verified = true };
        var sw = new Stopwatch(); var swm = new Stopwatch(); var zt = new double[6];
        double Lap(Stopwatch w, double before) => w.Elapsed.TotalMilliseconds - before;
        double footSum = 0; int footN = 0;
        int nIdx = 1_000_000;
        var idx = new long[nIdx]; var vals = new ulong[nIdx];
        for (int e = 0; e < epochs; e++)
        {
            bool measure = e >= 3;
            // заранее сгенерированные индексы, чтобы ГСЧ не попадал в замер
            long hotBase = Z(2) + (e / 4 % 8) * (z / 8);
            // 0: последовательное чтение
            double b0 = sw.Elapsed.TotalMilliseconds;
            if (measure) sw.Start();
            ulong s0 = a.Sum(Z(0), z) + a.Sum(Z(0), z);
            if (measure) sw.Stop();
            zt[0] += sw.Elapsed.TotalMilliseconds - b0;
            if (s0 != 2 * Kernels.SumU64(sh.AsSpan((int)Z(0), (int)z))) r.Verified = false;
            // 1: редкие случайные чтения
            for (int i = 0; i < 500_000; i++) idx[i] = Z(1) + rnd.NextInt64(z);
            ulong s1 = 0, e1 = 0;
            double b1 = sw.Elapsed.TotalMilliseconds;
            if (measure) sw.Start();
            for (int i = 0; i < 500_000; i++) s1 += a.Get(idx[i]);
            if (measure) sw.Stop();
            zt[1] += sw.Elapsed.TotalMilliseconds - b1;
            for (int i = 0; i < 500_000; i++) e1 += sh[idx[i]];
            if (s1 != e1) r.Verified = false;
            // 2: изменяется небольшой участок (64 строки × 64 слова)
            for (int i = 0; i < 300_000; i++) { idx[i] = hotBase + rnd.Next(64) * 512 + rnd.Next(64); vals[i] = rnd.Next(50) == 0 ? (ulong)rnd.Next(1 << 20) : (ulong)rnd.Next(1024); }
            ulong s2 = 0, e2 = 0;
            double b2 = sw.Elapsed.TotalMilliseconds;
            if (measure) sw.Start();
            for (int i = 0; i < 300_000; i++) { a.Set(idx[i], vals[i]); s2 += a.Get(idx[(i * 7) % 300_000]); }
            if (measure) sw.Stop();
            zt[2] += sw.Elapsed.TotalMilliseconds - b2;
            for (int i = 0; i < 300_000; i++) { sh[idx[i]] = vals[i]; e2 += sh[idx[(i * 7) % 300_000]]; }
            if (s2 != e2) r.Verified = false;
            // 3: частые случайные записи по всей зоне
            for (int i = 0; i < 400_000; i++) { idx[i] = Z(3) + rnd.NextInt64(z); vals[i] = (ulong)rnd.NextInt64(); }
            double b3 = sw.Elapsed.TotalMilliseconds;
            if (measure) sw.Start();
            for (int i = 0; i < 400_000; i++) a.Set(idx[i], vals[i]);
            if (measure) sw.Stop();
            zt[3] += sw.Elapsed.TotalMilliseconds - b3;
            for (int i = 0; i < 400_000; i++) sh[idx[i]] = vals[i];
            // 4: не трогаем. 5: разреженный — случайные чтения
            for (int i = 0; i < 200_000; i++) idx[i] = Z(5) + rnd.NextInt64(z);
            ulong s5 = 0, e5 = 0;
            double b5 = sw.Elapsed.TotalMilliseconds;
            if (measure) sw.Start();
            for (int i = 0; i < 200_000; i++) s5 += a.Get(idx[i]);
            if (measure) sw.Stop();
            zt[5] += sw.Elapsed.TotalMilliseconds - b5;
            for (int i = 0; i < 200_000; i++) e5 += sh[idx[i]];
            if (s5 != e5) r.Verified = false;

            if (measure) swm.Start();
            a.Maintain();
            if (measure) swm.Stop();
            if (measure) { footSum += a.Footprint; footN++; }
        }
        // полная сверка
        for (long i = 0; i < n; i += 997) if (a.Get(i) != sh[i]) { r.Verified = false; break; }
        for (int k = 0; k < 6; k++) if (a.Sum(Z(k), z) != Kernels.SumU64(sh.AsSpan((int)Z(k), (int)z))) r.Verified = false;
        int me = Math.Max(1, epochs - 3);
        r.OpsMs = sw.Elapsed.TotalMilliseconds / me;
        for (int k = 0; k < 6; k++) r.ZoneMs[k] = zt[k] / me;
        r.MaintainMs = swm.Elapsed.TotalMilliseconds / me;
        r.AvgFootprintMb = footSum / Math.Max(1, footN) / 1048576.0;
        r.Detail = a.Describe();
        return r;
    }

    public static string Format(List<AccessBenchResult> rs, CostModel? k = null)
    {
        var sb = new StringBuilder();
        if (k != null) sb.AppendLine("Измеренная модель стоимостей:\n" + k + "\n");
        double baseMs = rs.Count > 0 ? rs[0].OpsMs : 1;
        sb.AppendLine($"Логический объём {rs[0].LogicalMb:F0} МБ. Время — обращения за эпоху (среднее), политика — отдельно.");
        sb.AppendLine($"{"Стратегия",-58} {"Память МБ",10} {"% от обычн.",11} {"Обращения мс",13} {"× к обычн.",10} {"Политика мс",12} {"OK",4}");
        foreach (var r in rs)
            sb.AppendLine($"{r.Name,-58} {r.AvgFootprintMb,10:F1} {100 * r.AvgFootprintMb / r.LogicalMb,10:F0}% {r.OpsMs,13:F1} {r.OpsMs / baseMs,10:F2} {r.MaintainMs,12:F1} {(r.Verified ? "да" : "НЕТ"),4}");
        sb.AppendLine();
        sb.AppendLine("Время по зонам, мс/эпоху: 0 — последовательное чтение (2 прохода), 1 — редкие случайные чтения, 2 — горячий участок, 3 — случайные записи по всей зоне, 5 — разреженные чтения");
        foreach (var r in rs) sb.AppendLine($"  {r.Name,-58} 0: {r.ZoneMs[0],6:F1}  1: {r.ZoneMs[1],6:F1}  2: {r.ZoneMs[2],6:F1}  3: {r.ZoneMs[3],6:F1}  5: {r.ZoneMs[5],6:F1}");
        sb.AppendLine();
        foreach (var r in rs.Where(r => r.Detail != "")) sb.AppendLine($"■ {r.Name}\n   {r.Detail}");
        return sb.ToString();
    }
}
