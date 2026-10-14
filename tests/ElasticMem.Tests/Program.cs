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
fails += VmProviderTests.Run() + WhpFoundationTests.Run();
try { Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(ElasticMem.Core.Research.ResearchSelfTest.Run())); } catch(Exception ex) { Check(false, "research selftest: " + ex); }
string reportRoot=Path.Combine(Path.GetTempPath(),"ElasticMem_report_"+Guid.NewGuid().ToString("N"));
try
{
    using(var invalid=new ElasticMem.Core.Research.ResearchRun(reportRoot,"g4","test","fixture",new{}))
    {
        invalid.WriteJson("result.json",new{schema="elasticmem.g4.codecs.v1",codecs=new[]{new{codec="INVALID_NUMBER_SENTINEL",total_with_reserve_bytes=123}}});
        invalid.Seal("offline_codec_comparison_not_live_reclaim","fixture");invalid.Invalidate(new InvalidDataException("fixture"));
        var errors=new List<string>();string report=ElasticMem.App.Research.OneClickReport.Read(reportRoot,errors);
        Check(!report.Contains("INVALID_NUMBER_SENTINEL"),"invalid report numbers suppressed");Check(errors.Count==0,"invalid status reported without optional parse failure");
    }
    using(var broken=new ElasticMem.Core.Research.ResearchRun(reportRoot,"g4","test","fixture",new{}))
    {
        File.WriteAllText(broken.FilePath("result.json"),"{");
        broken.Seal("offline_codec_comparison_not_live_reclaim","fixture");
        var errors=new List<string>();string report=ElasticMem.App.Research.OneClickReport.Read(reportRoot,errors);
        Check(errors.Count==1&&report.Contains("недоступна"),"malformed optional report isolated,diagnostics retained");
    }
}
catch(Exception ex){Check(false,"report guard fixtures: "+ex);}
finally{if(Directory.Exists(reportRoot))Directory.Delete(reportRoot,true);}
reportRoot=Path.Combine(Path.GetTempPath(),"ElasticMem_open_summary_"+Guid.NewGuid().ToString("N"));
try
{
    using var summary=new ElasticMem.Core.Research.ResearchRun(reportRoot,"auto-summary","test","fixture",new{});
    using var good=new ElasticMem.Core.Research.ResearchRun(reportRoot,"g4","test","fixture",new{});
    good.WriteJson("result.json",new{schema="elasticmem.g4.codecs.v1",codecs=new[]{new{codec="VALID_NUMBER_SENTINEL",total_with_reserve_bytes=1048576,stored_payload_restore_p50_us_per_block=1.0,block_bytes=4096}}});
    good.Seal("offline_codec_comparison_not_live_reclaim","fixture");
    var errors=new List<string>();string report=ElasticMem.App.Research.OneClickReport.Read(reportRoot,errors,summary.DirectoryPath);
    Check(errors.Count==0&&report.Contains("VALID_NUMBER_SENTINEL"),"exact open summary excluded; successful numeric report retained");
    using var unfinished=new ElasticMem.Core.Research.ResearchRun(reportRoot,"g4","test","fixture",new{});
    errors.Clear();ElasticMem.App.Research.OneClickReport.Read(reportRoot,errors,summary.DirectoryPath);
    Check(errors.Count==1,"other unfinished run never silently excluded");
}
catch(Exception ex){Check(false,"open-summary fixtures: "+ex);}
finally{if(Directory.Exists(reportRoot))Directory.Delete(reportRoot,true);}
reportRoot=Path.Combine(Path.GetTempPath(),"ElasticMem_new_report_"+Guid.NewGuid().ToString("N"));
try
{
    using var codec=new ElasticMem.Core.Research.ResearchRun(reportRoot,"g4","test","fixture",new{});
    codec.WriteJson("result.json",new{schema="elasticmem.g4.codecs.v1",codecs=new[]{
        new{codec="xpress-4096",total_with_reserve_bytes=1000,stored_payload_restore_p50_us_per_block=2.0,block_bytes=4096},
        new{codec="zstd1-4096",total_with_reserve_bytes=800,stored_payload_restore_p50_us_per_block=6.0,block_bytes=4096},
        new{codec="zstd1-16384",total_with_reserve_bytes=700,stored_payload_restore_p50_us_per_block=17.0,block_bytes=16384},
        new{codec="zstd1-dictionary-4096",total_with_reserve_bytes=840,stored_payload_restore_p50_us_per_block=5.0,block_bytes=4096}}});
    codec.Seal("offline_codec_comparison_not_live_reclaim","fixture");
    using var epochs=new ElasticMem.Core.Research.ResearchRun(reportRoot,"epochs-replay","test","fixture",new{});
    epochs.WriteJson("result.json",new{schema="elasticmem.epochs.replay.v1",processes=new[]{new{
        pid=123,baseline_residency_available=true,causal_cohort_pages=16,trace_health_available=false,
        diagnostic=new{primary_cohort="initially_resident_all_known",model=new{
            budget_us_per_second=10000,scenario_best=new[]{new{miss_cost_us=100,maximum_among_tested_thresholds_bytes=1048576}},
            curves=new[]{new{idle_epochs=64,average_end_epoch_offloaded_bytes=1048576,modeled_reacquisitions_per_second=10,misses_per_epoch_p99=20,misses_per_epoch_max=8000}}}},
        causal=new{rows=new[]{new{excluded_common_unknown_epochs=4,test_epochs=300,gate_same_epoch_only=(bool?)null}}}}}});
    epochs.Seal("offline_epoch_replay_not_physical_ram","fixture");
    using var disk=new ElasticMem.Core.Research.ResearchRun(reportRoot,"disk-qd1","test","fixture",new{});
    disk.WriteJson("result.json",new{schema="elasticmem.disk.qd1.v1",groups=new[]{new{bytes=65536,n=100,p50_us=400.0,p95_us=500.0}}});
    disk.Seal("unbuffered_file_qd1_not_pagefile_fault","fixture");
    using var gpu=new ElasticMem.Core.Research.ResearchRun(reportRoot,"g3-dma","test","fixture",new{});
    gpu.WriteJson("result.json",new{schema="elasticmem.g3.dma.v1",groups=new[]{
        new{mode="restore",bytes=65536,n=100,p50_us=80.0,p95_us=120.0},
        new{mode="roundtrip",bytes=65536,n=100,p50_us=150.0,p95_us=200.0}}});
    gpu.Seal("dma_microbenchmark_not_pager","fixture");
    var errors=new List<string>();string report=ElasticMem.App.Research.OneClickReport.Read(reportRoot,errors);
    Check(errors.Count==0,"new numeric report fixtures parse");
    Check(report.Contains("20.00%; порог размера ≥25% НЕ взят"),"4K size gate fails below25");
    Check(report.Contains("30.00%; порог размера ≥25% взят"),"16K only size gate passes");
    Check(report.Contains("plain zstd4K=5.00%"),"dictionary penalty denominator");
    Check(report.Contains("unknown TEST epochs=4/300; gate unavailable"),"unknown not negative gate");
    Check(report.Contains("2.00/800.00 мс,НЕ измеренные задержки"),"scenario burst not latency");
    Check(report.Contains("FILE/GPU медиан=5.00×"),"file restore ratio exact");
    Check(report.Contains("НЕ взята только для roundtrip"),"roundtrip not restore median gate");
}
catch(Exception ex){Check(false,"new report fixtures: "+ex);}
finally{if(Directory.Exists(reportRoot))Directory.Delete(reportRoot,true);}
reportRoot=Path.Combine(Path.GetTempPath(),"ElasticMem_fresh_snapshot_"+Guid.NewGuid().ToString("N"));
try
{
    using var source=new ElasticMem.Core.Research.ResearchRun(reportRoot,"epoch-snapshot","test","fixture",new{});
    source.WriteJson("snapshots/123/inventory.json",new{pid=123,process_name="chrome",start_ticks=111L,pages=new[]{new{address=4096UL,file_offset=0}}});
    File.WriteAllBytes(source.FilePath("snapshots/123/pages.bin"),new byte[4096]);
    source.Seal("fresh_renderer_snapshot_checked","synthetic fixture");
    using var dest=new ElasticMem.Core.Research.ResearchRun(reportRoot,"load","test","fixture",new{});
    var input=ElasticMem.App.Research.SnapshotInputs.Load(source.DirectoryPath,dest,CancellationToken.None,123);
    Check(input.Length==1&&input[0].Pid==123&&input[0].StartTicks==111&&input[0].Addresses[0]==4096,"fresh snapshot binds source identity/address");
    void Reject(int pid,string label){bool rejected=false;try{ElasticMem.App.Research.SnapshotInputs.Load(source.DirectoryPath,dest,CancellationToken.None,pid);}catch(ArgumentException){rejected=true;}Check(rejected,label);}
    Reject(0,"single snapshot not a codec cross-process source");
    Reject(124,"fresh source target cannot be substituted");
    Reject(-1,"negative snapshot PID rejected");
    File.WriteAllBytes(Path.Combine(source.DirectoryPath,"snapshots/123/pages.bin"),new byte[8192]);
    bool tamper=false;try{ElasticMem.App.Research.SnapshotInputs.Load(source.DirectoryPath,dest,CancellationToken.None,123);}catch(InvalidDataException){tamper=true;}
    Check(tamper,"fresh tampered snapshot refused before decode");
}
catch(Exception ex){Check(false,"fresh source fixtures: "+ex);}
finally{if(Directory.Exists(reportRoot))Directory.Delete(reportRoot,true);}
reportRoot=Path.Combine(Path.GetTempPath(),"ElasticMem_control_report_"+Guid.NewGuid().ToString("N"));
try
{
    using var controlled=new ElasticMem.Core.Research.ResearchRun(reportRoot,"controlled","test","fixture",new{});
    var cr=new{rows=new[]{new{idle_epochs=2,block_pages=4,policy="coaccess",mean_useful_pages_per_decode=3.0,decode_events=40,excluded_common_unknown_epochs=1}}};
    controlled.WriteJson("result.json",new{schema="elasticmem.controlled.research.v1",cases=new[]{new{name="fixture",observation=new{
        known_fraction=.75,unknown_test_epochs=1,known_false_negative=3,known_false_positive=4},ground_truth_causal=cr,windows_proxy_causal=cr}}});
    // Version2 result must render lifecycle separately,not as a positive causal gate.
    var controlResult=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(controlled.FilePath("result.json")))!;
    controlResult["schema"]="elasticmem.controlled.research.v2";
    controlResult["lifecycle"]=System.Text.Json.JsonSerializer.SerializeToNode(new{active_page_epoch_cells=28672,dead_page_epoch_cells=4000,reserved_never_committed_page_epoch_cells=96,reserved_reported_valid_before=0,reserved_reported_valid_after=2,retained_active_unknown_cells=3,known_false_negative=1,known_false_positive=2,committed_pages=1000,decommitted_pages=900,dead_reported_valid_before=0,dead_reported_valid_after=1,checksum_verified=true});
    File.WriteAllText(controlled.FilePath("result.json"),controlResult.ToJsonString());
    File.WriteAllText(controlled.FilePath("inputs/controlled-child.json"),"private worker IPC fixture");
    controlled.Seal("controlled_synthetic_validation_not_browser_evidence","fixture");
    var errors=new List<string>();string report=ElasticMem.App.Research.OneClickReport.Read(reportRoot,errors);
    Check(errors.Count==0,"controlled numeric report available");
    Check(report.Contains("known cells=75.00%")&&report.Contains("unknown TEST epochs=1; FN=3; FP=4"),"controlled missingness explicit");
    Check(report.Contains("ground_truth_causal T2/cap4")&&report.Contains("windows_proxy_causal T2/cap4"),"truth vs observed distinguished");
    Check(report.Contains("не подтверждает пользу в Chrome"),"synthetic report no browser effectiveness claim");
    Check(report.Contains("Lifecycle decommit/recommit: active=28672; dead=4000; reserved-only=96; retained unknown=3; FN/FP=1/2"),"lifecycle report preserves disjoint populations");
    Check(report.Contains("Г5 lifecycle skipped")&&report.Contains("dead reported Valid before/after=0/1"),"lifecycle report no fake causal gate or hidden dead anomaly");
}
catch(Exception ex){Check(false,"controlled report fixture: "+ex);}
finally{if(Directory.Exists(reportRoot))Directory.Delete(reportRoot,true);}
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
return fails == 0 ? 0 : 1;
