using System.Diagnostics;
using System.Text;

namespace ElasticMem.Core;

public sealed class BenchmarkResult
{
    public string Name = "";
    public long LogicalSize;
    public double AvgFootprint, PeakFootprint, FinalFootprint;
    public double WallMs, AvgReadNs, MaxReadUs, AvgWriteNs;
    public StoreStats Stats = new();
    public bool Verified;
    public long Mismatches;
    public IReadOnlyDictionary<string, int> Formats = new Dictionary<string, int>();
}

public sealed class BenchmarkConfig
{
    public int BlockSize = 1024;
    public double DurationSec = 30;
    public double TickSec = 0.02;
    public int SegmentKb = 512;
    public int Seed = 42;
}

/// <summary>
/// Тестовые потоки изменений: координаты, счётчики, таблица, разреженные данные, текст, шум, горячий буфер.
/// Все стратегии получают одинаковую последовательность операций; каждое чтение сверяется с эталоном (без потерь).
/// </summary>
public static class Benchmark
{
    public static List<Func<long, IMemoryStore>> DefaultStrategies(BenchmarkConfig c) => new()
    {
        size => new PageStore(size, 4096, 0.5),
        size => new ElasticStore(size, new ElasticStoreOptions
        {
            Name = $"Микроблоки {c.BlockSize} Б: адаптивные форматы", BlockSize = c.BlockSize,
            EnablePatches = false, LifetimeAware = false,
        }),
        size => new ElasticStore(size, new ElasticStoreOptions
        {
            Name = $"Основа (Deflate) + патчи, {c.BlockSize} Б", BlockSize = c.BlockSize,
            Codec = new CodecOptions { EnablePacked = false, EnableSparse = false, EnableLz = true },
            EnablePatches = true, LifetimeAware = false,
        }),
        size => new ElasticStore(size, new ElasticStoreOptions
        {
            Name = $"Микроблоки + патчи + прогноз срока жизни", BlockSize = c.BlockSize,
            Codec = new CodecOptions { Headrooms = new[] { 0, 2, 4, 8 } },
            EnablePatches = true, LifetimeAware = true,
        }),
    };

    private enum Seg { Coords, Counters, Table, Sparse, Text, Noise, HotBuffer }

    public static List<BenchmarkResult> Run(BenchmarkConfig cfg, Action<string>? progress = null, List<Func<long, IMemoryStore>>? strategies = null)
    {
        strategies ??= DefaultStrategies(cfg);
        var results = new List<BenchmarkResult>();
        foreach (var make in strategies)
        {
            long segSize = cfg.SegmentKb * 1024L;
            int segCount = Enum.GetValues<Seg>().Length;
            var store = make(segSize * segCount);
            progress?.Invoke("Запуск: " + store.Name);
            results.Add(RunOne(store, cfg, segSize));
        }
        return results;
    }

    private static BenchmarkResult RunOne(IMemoryStore store, BenchmarkConfig cfg, long segSize)
    {
        var rnd = new Random(cfg.Seed);
        long size = store.LogicalSize;
        var shadow = new byte[size];
        var res = new BenchmarkResult { Name = store.Name, LogicalSize = size };
        var buf = new byte[4096];
        var rbuf = new byte[4096];
        long readTicks = 0, readCount = 0, maxRead = 0, writeTicks = 0, writeCount = 0;
        double footSum = 0; int footN = 0;
        var sw = Stopwatch.StartNew();

        void W(long off, ReadOnlySpan<byte> data)
        {
            long t0 = Stopwatch.GetTimestamp();
            store.Write(off, data);
            writeTicks += Stopwatch.GetTimestamp() - t0; writeCount++;
            data.CopyTo(shadow.AsSpan((int)off));
        }
        void W64(long off, ulong v) { BitConverter.TryWriteBytes(buf, v); W(off, buf.AsSpan(0, 8)); }
        ulong R64(long off) => BitConverter.ToUInt64(shadow, (int)off);
        long Base(Seg s) => (long)s * segSize;
        int words = (int)(segSize / 8);

        // --- начальное заполнение ---
        store.Now = 0;
        for (int i = 0; i < words; i++) W64(Base(Seg.Coords) + i * 8, (ulong)rnd.Next(4096));
        for (int i = 0; i < words; i++) W64(Base(Seg.Counters) + i * 8, (ulong)rnd.Next(4));
        for (int r = 0; r < words / 4; r++)
        {
            long o = Base(Seg.Table) + r * 32;
            W64(o, (ulong)(100000 + r)); W64(o + 8, (ulong)rnd.Next(8)); W64(o + 16, (ulong)rnd.Next(1000, 100000)); W64(o + 24, 0);
        }
        for (int i = 0; i < words / 20; i++) W64(Base(Seg.Sparse) + rnd.Next(words) * 8L, (ulong)rnd.NextInt64(1, long.MaxValue));
        string[] logWords = { "INFO", "WARN", "user", "login", "request", "GET /api/items", "status=200", "latency=", "ms", "session", "cache hit", "cache miss" };
        {
            var sb = new StringBuilder();
            while (sb.Length < segSize) sb.Append(logWords[rnd.Next(logWords.Length)]).Append(' ').Append(rnd.Next(1000)).Append('\n');
            var bytes = Encoding.ASCII.GetBytes(sb.ToString(0, (int)segSize));
            for (int o = 0; o < segSize; o += 4096) W(Base(Seg.Text) + o, bytes.AsSpan(o, 4096));
        }
        for (long o = 0; o < segSize; o += 4096) { rnd.NextBytes(buf); W(Base(Seg.Noise) + o, buf); }

        // фазы активности: (период, доля активной части, сдвиг)
        var phases = new Dictionary<Seg, (double period, double duty, double shift)>
        {
            [Seg.Coords] = (8, 0.4, 0), [Seg.Counters] = (6, 0.5, 2), [Seg.Table] = (10, 0.3, 4),
            [Seg.Sparse] = (7, 0.4, 1), [Seg.Text] = (9, 0.3, 3), [Seg.Noise] = (5, 0.2, 0), [Seg.HotBuffer] = (1, 1, 0),
        };
        bool Active(Seg s, double t) { var p = phases[s]; return ((t + p.shift) % p.period) < p.period * p.duty; }

        int ticks = (int)(cfg.DurationSec / cfg.TickSec);
        for (int k = 1; k <= ticks; k++)
        {
            double t = k * cfg.TickSec;
            store.Now = t;
            if (Active(Seg.Coords, t))
                for (int j = 0; j < 150; j++)
                {
                    // активный «кластер» объектов, координаты дрейфуют в пределах диапазона
                    long o = Base(Seg.Coords) + (rnd.Next(words / 8) + (int)(t / 8) % 8 * (words / 8)) * 8L;
                    long v = (long)R64(o) + rnd.Next(-3, 4);
                    W64(o, (ulong)Math.Clamp(v, 0, 4095));
                }
            if (Active(Seg.Counters, t))
                for (int j = 0; j < 150; j++)
                {
                    long o = Base(Seg.Counters) + rnd.Next(words / 4) * 8L; // растут медленно
                    W64(o, R64(o) + 1);
                }
            if (Active(Seg.Table, t))
                for (int j = 0; j < 60; j++)
                {
                    long o = Base(Seg.Table) + rnd.Next(words / 4) * 32L;
                    W64(o + 8, (ulong)rnd.Next(8)); W64(o + 16, (ulong)rnd.Next(1000, 100000));
                }
            if (Active(Seg.Sparse, t))
                for (int j = 0; j < 40; j++)
                {
                    long o = Base(Seg.Sparse) + rnd.Next(words) * 8L;
                    W64(o, R64(o) != 0 ? (ulong)rnd.NextInt64(1, long.MaxValue) : 0);
                }
            if (Active(Seg.Text, t))
                for (int j = 0; j < 8; j++)
                {
                    var s = Encoding.ASCII.GetBytes(logWords[rnd.Next(logWords.Length)]);
                    W(Base(Seg.Text) + rnd.Next((int)segSize - 32), s);
                }
            if (Active(Seg.Noise, t))
                for (int j = 0; j < 4; j++) { rnd.NextBytes(buf.AsSpan(0, 64)); W(Base(Seg.Noise) + rnd.Next((int)segSize / 64) * 64L, buf.AsSpan(0, 64)); }
            // горячий рабочий буфер: 32 КБ перезаписываются каждый тик
            for (int o = 0; o < 32768; o += 4096) { rnd.NextBytes(buf); W(Base(Seg.HotBuffer) + o, buf); }

            // чтения: часть — по активным сегментам, часть — случайные
            for (int j = 0; j < 120; j++)
            {
                int len = 8 << rnd.Next(4);
                long off;
                if (rnd.Next(10) == 0) off = rnd.NextInt64(size - len); // 10% — случайные
                else
                {
                    // 90% — по активным сейчас сегментам (рабочее множество)
                    var act = Enum.GetValues<Seg>().Where(s => Active(s, t)).ToArray();
                    var sg = act[rnd.Next(act.Length)];
                    off = Base(sg) + rnd.NextInt64(segSize - len);
                }
                long t0 = Stopwatch.GetTimestamp();
                store.Read(off, rbuf.AsSpan(0, len));
                long dt = Stopwatch.GetTimestamp() - t0;
                readTicks += dt; readCount++; if (dt > maxRead) maxRead = dt;
                if (!rbuf.AsSpan(0, len).SequenceEqual(shadow.AsSpan((int)off, len))) res.Mismatches++;
            }
            store.Tick();
            long f = store.Footprint;
            footSum += f; footN++;
            if (f > res.PeakFootprint) res.PeakFootprint = f;
        }
        res.WallMs = sw.Elapsed.TotalMilliseconds;
        res.Formats = store.FormatHistogram();
        res.FinalFootprint = store.Footprint;

        // финальная полная сверка без потерь
        var all = new byte[65536];
        for (long o = 0; o < size; o += all.Length)
        {
            int len = (int)Math.Min(all.Length, size - o);
            store.Read(o, all.AsSpan(0, len));
            if (!all.AsSpan(0, len).SequenceEqual(shadow.AsSpan((int)o, len))) res.Mismatches++;
        }
        res.Verified = res.Mismatches == 0;
        res.AvgFootprint = footSum / Math.Max(1, footN);
        res.AvgReadNs = readTicks * 1e9 / Stopwatch.Frequency / Math.Max(1, readCount);
        res.AvgWriteNs = writeTicks * 1e9 / Stopwatch.Frequency / Math.Max(1, writeCount);
        res.MaxReadUs = maxRead * 1e6 / Stopwatch.Frequency;
        res.Stats = store.Stats;
        return res;
    }

    public static string Format(List<BenchmarkResult> rs)
    {
        var sb = new StringBuilder();
        long logical = rs.Count > 0 ? rs[0].LogicalSize : 0;
        sb.AppendLine($"Логический объём: {logical / 1024.0 / 1024:F2} МБ (учитывается ВСЯ занятая память: данные, сжатые блоки, патчи, метаданные)");
        sb.AppendLine();
        sb.AppendLine($"{"Стратегия",-46} {"Средн. МБ",10} {"Пик МБ",8} {"Экон.%",7} {"Чтение нс",10} {"Макс мкс",9} {"Запись нс",10} {"Упак.МБ",8} {"Расп.МБ",8} {"Время мс",9} {"OK",4}");
        foreach (var r in rs)
        {
            double econ = 100 * (1 - r.AvgFootprint / r.LogicalSize);
            sb.AppendLine($"{Trunc(r.Name, 46),-46} {r.AvgFootprint / 1048576,10:F2} {r.PeakFootprint / 1048576,8:F2} {econ,7:F1} {r.AvgReadNs,10:F0} {r.MaxReadUs,9:F0} {r.AvgWriteNs,10:F0} {r.Stats.EncodedBytes / 1048576.0,8:F1} {r.Stats.DecodedBytes / 1048576.0,8:F1} {r.WallMs,9:F0} {(r.Verified ? "да" : "НЕТ"),4}");
        }
        sb.AppendLine();
        foreach (var r in rs)
        {
            var s = r.Stats;
            sb.AppendLine($"■ {r.Name}");
            sb.AppendLine($"   упаковок {s.Compressions}, распаковок {s.Promotions} (ранних {s.EarlyPromotions}), новых основ {s.Rebases}; " +
                          $"записей: в сжатый блок напрямую {s.InPlaceWrites}, патчами {s.PatchWrites}, в обычный {s.HotWrites}");
            sb.AppendLine("   форматы в конце прогона: " + string.Join(", ", r.Formats.OrderByDescending(x => x.Value).Select(x => $"{x.Key}={x.Value}")));
        }
        return sb.ToString();
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
