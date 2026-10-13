using ElasticMem.Core.Access;

public static class ArrayTests
{
    public static int Run()
    {
        int fails = 0;
        var rnd = new Random(3);
        foreach (bool narrow in new[] { false, true })
        foreach (bool aware in new[] { true, false })
        foreach (double budget in new[] { 1.0, 0.4 })
        {
            long n = 300_000;
            var a = new ElasticArray64(n, new ElasticArrayOptions { AccessAware = aware, BudgetBytes = (long)(n * 8 * budget), MinDwellEpochs = 1, DenseAfterIdleEpochs = 1 });
            var sh = new ulong[n];
            for (int epoch = 0; epoch < 40; epoch++)
            {
                int mode = epoch % 5;
                for (int s = 0; s < 40_000; s++)
                {
                    long i = mode == 0 ? rnd.NextInt64(n) : mode == 1 ? (epoch * 7919L % n + rnd.Next(5000)) % n : rnd.NextInt64(n / 3);
                    if (rnd.Next(3) == 0)
                    {
                        ulong v = narrow ? (rnd.Next(60) == 0 ? (ulong)rnd.NextInt64() : rnd.Next(40) == 0 ? 0UL : 1_000_000UL + (ulong)rnd.Next(narrow && epoch % 2 == 0 ? 200 : 70000)) : rnd.Next(4) switch { 0 => 0, 1 => (ulong)rnd.Next(300), 2 => 1_000_000UL + (ulong)rnd.Next(70000), _ => (ulong)rnd.NextInt64() };
                        a.Set(i, v); sh[i] = v;
                    }
                    else if (a.Get(i) != sh[i]) { fails++; Console.WriteLine($"FAIL get aware={aware} epoch={epoch}"); goto next; }
                }
                long st = rnd.NextInt64(n - 5000);
                ulong ex = 0; for (long i = st; i < st + 5000; i++) ex += sh[i];
                if (a.Sum(st, 5000) != ex) { fails++; Console.WriteLine("FAIL sum"); goto next; }
                var buf = new ulong[3000]; a.ReadRange(st, buf);
                for (int i = 0; i < buf.Length; i++) if (buf[i] != sh[st + i]) { fails++; Console.WriteLine("FAIL range"); goto next; }
                if (epoch > 20 && epoch % 7 == 0) a.ForceAll((Rep)(epoch % 5));
                if (narrow && epoch % 3 == 0) { for (long i = 0; i < n; i++) if (sh[i] > 2_000_000) { sh[i] = 1_000_000; a.Set(i, sh[i]); } a.ForceAll(epoch % 2 == 0 ? Rep.Packed : Rep.Aligned); }
                a.Maintain();
            }
            for (long i = 0; i < n; i++) if (a.Get(i) != sh[i]) { fails++; Console.WriteLine("FAIL final"); break; }
            Console.WriteLine($"ElasticArray64 narrow={narrow} aware={aware} budget={budget}: footprint {a.Footprint / 1024} KB of {n * 8 / 1024} KB, reps [{string.Join(",", a.Stats.ChunksByRep)}]");
            next:;
        }
        return fails;
    }
}
