using ElasticMem.Core;

int fails = 0;
void Check(bool ok, string what) { if (!ok) { fails++; Console.WriteLine("FAIL: " + what); } }

if (args.Contains("--skip-tests")) goto bench;
// 1. Кодеки: все кандидаты обратимы
var rnd = new Random(1);
var gens = new Func<int, ulong>[] {
    i => 0, i => (ulong)rnd.Next(4096), i => 1_000_000UL + (ulong)rnd.Next(100),
    i => rnd.Next(10) == 0 ? (ulong)rnd.NextInt64() : 0, i => (ulong)rnd.NextInt64(),
    i => rnd.Next(20) == 0 ? ulong.MaxValue - (ulong)rnd.Next(5) : (ulong)rnd.Next(300),
};
var opt = new CodecOptions { Headrooms = new[] { 0, 2, 4, 8 } };
foreach (int bs in new[] { 256, 1024, 4096 })
    foreach (var g in gens)
        for (int rep = 0; rep < 20; rep++)
        {
            var block = new byte[bs];
            for (int i = 0; i < bs / 8; i++) BitConverter.TryWriteBytes(block.AsSpan(i * 8), g(i));
            foreach (var c in BlockCodec.Candidates(block, opt))
            {
                var e = BlockCodec.Build(block, c, opt);
                var dst = new byte[bs];
                BlockCodec.Decode(e, dst);
                Check(dst.AsSpan().SequenceEqual(block), $"roundtrip {c.Format} bs={bs}");
                if (c.Format != BlockFormat.Lz && c.Format != BlockFormat.Raw)
                    Check(e.SizeBytes == c.SizeBytes, $"size {c.Format} {e.SizeBytes} vs {c.SizeBytes}");
                if (e.SupportsWordAccess)
                    for (int i = 0; i < bs / 8; i++) Check(BlockCodec.GetWord(e, i) == BitConverter.ToUInt64(block, i * 8), "getword");
            }
        }

// 2. Хранилища: случайные операции против эталона
foreach (var mk in new Func<long, IMemoryStore>[] {
    s => new PageStore(s),
    s => new ElasticStore(s, new ElasticStoreOptions { BlockSize = 256, LifetimeAware = false, EnablePatches = false }),
    s => new ElasticStore(s, new ElasticStoreOptions { BlockSize = 1024 }),
    s => new ElasticStore(s, new ElasticStoreOptions { BlockSize = 512, MinDwellSec = 0, Codec = new CodecOptions { Headrooms = new[] { 0, 3 } } }),
})
{
    var st = mk(256 * 1024);
    var sh = new byte[st.LogicalSize];
    var r = new Random(7);
    var buf = new byte[600];
    for (int step = 0; step < 200_000; step++)
    {
        st.Now = step * 0.001;
        int len = r.Next(1, 600);
        long off = r.NextInt64(st.LogicalSize - len);
        if (r.Next(3) == 0)
        {
            int kind = r.Next(3);
            for (int i = 0; i < len; i++) buf[i] = kind == 0 ? (byte)0 : kind == 1 ? (byte)r.Next(4) : (byte)r.Next(256);
            st.Write(off, buf.AsSpan(0, len)); buf.AsSpan(0, len).CopyTo(sh.AsSpan((int)off));
        }
        else
        {
            st.Read(off, buf.AsSpan(0, len));
            if (!buf.AsSpan(0, len).SequenceEqual(sh.AsSpan((int)off, len))) { Check(false, st.Name + " read mismatch @" + step); break; }
        }
        if (step % 50 == 0) st.Tick();
    }
    Console.WriteLine($"{st.Name}: footprint {st.Footprint / 1024} KB, compressions {st.Stats.Compressions}, inplace {st.Stats.InPlaceWrites}, patch {st.Stats.PatchWrites}");
}
fails += ArrayTests.Run();
fails += PageStoreSampleTests.Run();
fails += BackendTests.Run() + VmBackendTests.Run() + VmCreationTests.Run();
fails += VmProviderTests.Run() + UserStoreTests.Run() + RamSaverPolicyTests.Run() + RealtimeHotSetTests.Run() + CompressedPageTableTests.Run() + CodecBakeoffTests.Run();
fails += DedupStoreTests.Run();
try { Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(ElasticMem.Core.Research.ResearchSelfTest.Run())); } catch (Exception ex) { Check(false, "research selftest: " + ex); }
Console.WriteLine(fails == 0 ? "ALL TESTS PASSED" : $"{fails} FAILURES");

bench:
if (args.Contains("--bench"))
    Console.WriteLine(Benchmark.Format(Benchmark.Run(new BenchmarkConfig { DurationSec = 20 }, Console.WriteLine)));
if (args.Contains("--selftest")) Console.WriteLine(ElasticMem.Core.SelfTest.Run());
int insightIdx = Array.IndexOf(args, "--insight");
if (insightIdx >= 0)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var mi = new ElasticMem.Core.Access.MemoryInsight(48L << 20);
    var data = File.ReadAllBytes(args[insightIdx + 1]);
    for (int o = 0; o < data.Length; o += 1 << 20) mi.Feed(data.AsSpan(o, Math.Min(1 << 20, data.Length - o)));
    Console.WriteLine(ElasticMem.Core.Access.MemoryInsight.Format(args[insightIdx + 1], mi.Report()));
    Console.WriteLine($"время {sw.Elapsed.TotalSeconds:F1} с, {data.Length / 1048576.0 / sw.Elapsed.TotalSeconds:F0} МБ/с");
}
if (args.Contains("--linked"))
    Console.WriteLine(ElasticMem.Core.Access.LinkedExperiment.Format(ElasticMem.Core.Access.LinkedExperiment.Run(log: Console.WriteLine)));
if (args.Contains("--micro")) Console.WriteLine(ElasticMem.Core.Access.AccessBench.Micro());
if (args.Contains("--access"))
{
    var k = ElasticMem.Core.Access.Calibration.Measure(log: Console.WriteLine);
    var r = ElasticMem.Core.Access.AccessBench.Run(log: Console.WriteLine, costs: k);
    Console.WriteLine(ElasticMem.Core.Access.AccessBench.Format(r, k));
}
int dedupIdx = Array.IndexOf(args, "--dedup-ceiling");
if (dedupIdx >= 0)
{
    if (dedupIdx + 1 < args.Length && Directory.Exists(args[dedupIdx + 1]))
    {
        var pages = new List<byte[]>();
        foreach (var f in Directory.EnumerateFiles(args[dedupIdx + 1], "*.bin", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(f);
            for (int o = 0; o + ElasticMem.Core.Research.DedupAnalyzer.PageBytes <= bytes.Length; o += ElasticMem.Core.Research.DedupAnalyzer.PageBytes)
                pages.Add(bytes.AsSpan(o, ElasticMem.Core.Research.DedupAnalyzer.PageBytes).ToArray());
        }
        Console.WriteLine($"DEDUP CEILING: {pages.Count} страниц из {args[dedupIdx + 1]}");
        Console.WriteLine(ElasticMem.Core.Research.DedupAnalyzer.Format(ElasticMem.Core.Research.DedupAnalyzer.Measure(pages)));
    }
    else Console.WriteLine(ElasticMem.Core.Research.DedupAnalyzer.SelfTest());
}
return fails == 0 ? 0 : 1;
