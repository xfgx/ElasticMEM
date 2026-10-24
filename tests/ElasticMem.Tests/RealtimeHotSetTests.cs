using ElasticMem.Core;
static class RealtimeHotSetTests
{
    public static int Run()
    {
        int checks = 0, fails = 0; void C(bool ok, string m) { checks++; if (!ok) { fails++; Console.WriteLine("FAIL REALTIME: " + m); } }
        const ulong R = RealtimeHotSet.RegionBytes;

        C(RealtimeHotSet.RegionOf(0) == 0 && RealtimeHotSet.RegionOf(R - 1) == 0 && RealtimeHotSet.RegionOf(R) == 1, "region bucketing by 64 KiB");

        // Регион, тронутый каждый интервал, становится горячим; тронутый один раз — затухает и остывает.
        var h = new RealtimeHotSet(decay: 0.5, hotThreshold: 0.35);
        for (int i = 0; i < 5; i++) h.Observe(new ulong[] { 10 });
        C(h.ScoreOf(10) > 0.9, "always-accessed region saturates hot");
        h.Observe(new ulong[] { 99 });            // 10 пропущен один раз
        for (int i = 0; i < 6; i++) h.Observe(new ulong[] { 99 });
        C(h.ScoreOf(10) < 0.1, "unaccessed region decays cold");
        C(h.ScoreOf(99) > 0.9, "newly hot region rises");

        // HotRegions возвращает именно горячие.
        var hot = h.HotRegions();
        C(hot.Contains(99) && !hot.Contains(10), "hot set reflects current hotness");

        // Группировка: регионы, которые всегда вместе, подтягиваются как группа, даже если один чуть «холоднее».
        var g = new RealtimeHotSet(decay: 0.5, hotThreshold: 0.35);
        for (int i = 0; i < 6; i++) g.Observe(new ulong[] { 1, 2, 3 });
        var gh = g.HotRegions();
        C(gh.Contains(1) && gh.Contains(2) && gh.Contains(3), "co-accessed group kept together");

        // Сосед, который почти никогда не встречается вместе, в группу не попадает.
        var g2 = new RealtimeHotSet(decay: 0.5, hotThreshold: 0.35);
        for (int i = 0; i < 10; i++) g2.Observe(new ulong[] { 1 });
        g2.Observe(new ulong[] { 1, 77 });          // 77 — случайный одиночный сосед
        for (int i = 0; i < 4; i++) g2.Observe(new ulong[] { 1 }); // 77 остывает сам
        C(g2.HotRegions().Contains(1) && !g2.HotRegions().Contains(77), "rare neighbor not promoted into group");

        // Обратная связь: стресс снижает порог (держим больше горячим), покой — повышает.
        var f = new RealtimeHotSet(decay: 0.5, hotThreshold: 0.4);
        double t0 = f.HotThreshold; f.Feedback(responseMs: 200, refaultRate: 0.9); C(f.HotThreshold < t0, "stress lowers threshold");
        double t1 = f.HotThreshold; for (int i = 0; i < 10; i++) f.Feedback(responseMs: 1, refaultRate: 0.01); C(f.HotThreshold > t1, "calm raises threshold");
        C(f.HotThreshold <= 0.8 + 1e-9, "threshold capped high");
        var f2 = new RealtimeHotSet(decay: 0.5, hotThreshold: 0.12);
        for (int i = 0; i < 20; i++) f2.Feedback(responseMs: 500, refaultRate: 1); C(f2.HotThreshold >= 0.1 - 1e-9, "threshold floored low");

        // Порог адаптации виден в составе горячего набора: при низком пороге горячих регионов больше.
        var lo = new RealtimeHotSet(decay: 0.5, hotThreshold: 0.1);
        var hi = new RealtimeHotSet(decay: 0.5, hotThreshold: 0.6);
        for (int i = 0; i < 3; i++) { lo.Observe(new ulong[] { 5 }); hi.Observe(new ulong[] { 5 }); }
        lo.Observe(Array.Empty<ulong>()); hi.Observe(Array.Empty<ulong>()); // чуть остудить
        C(lo.HotRegions().Count >= hi.HotRegions().Count, "lower threshold keeps at least as many hot");

        // Пустые наблюдения не падают и всё остужают.
        var e = new RealtimeHotSet();
        for (int i = 0; i < 3; i++) e.Observe(Array.Empty<ulong>());
        C(e.HotRegions().Count == 0, "empty observations keep nothing hot");

        // Память ограничена: 4000 тронутых регионов за интервал раньше давали 16 млн пар. Теперь — не больше
        // MaxCoaccessSample^2 пар за интервал и MaxNeighbors соседей на регион.
        var big = new RealtimeHotSet();
        var many = Enumerable.Range(0, 4000).Select(i => (ulong)i).ToArray();
        for (int i = 0; i < 20; i++) big.Observe(many);
        C(big.CoaccessEntries <= 4000L * RealtimeHotSet.MaxNeighbors, "co-access memory bounded by regions*MaxNeighbors");
        C(big.CoaccessEntries < 4000L * 4000L / 100, "co-access far below all-pairs");
        var sw = System.Diagnostics.Stopwatch.StartNew(); big.Observe(many); sw.Stop();
        C(sw.ElapsedMilliseconds < 2000, "one interval with 4000 regions stays cheap (" + sw.ElapsedMilliseconds + " ms)");

        // Не трогаем процесс, если набор почти не вырос (иначе он распаковывает своё же горячее — лишний CPU).
        const long Mb = 1L << 20;
        C(RealtimeHotSet.NeedsCompaction(100 * Mb, 0), "first time: compact");
        C(!RealtimeHotSet.NeedsCompaction(105 * Mb, 100 * Mb), "small growth: skip");
        C(RealtimeHotSet.NeedsCompaction(130 * Mb, 100 * Mb), "grew >25%: compact");
        C(!RealtimeHotSet.NeedsCompaction(10 * Mb, 4 * Mb), "tiny process +6MB under 8MB floor: skip");
        C(RealtimeHotSet.NeedsCompaction(13 * Mb, 4 * Mb), "tiny process +9MB over floor: compact");

        Console.WriteLine($"REALTIME HOTSET {checks} checks; pure policy, NOT native EmptyWorkingSet/Prefetch");
        return fails;
    }
}
