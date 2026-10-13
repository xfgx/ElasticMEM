using System.Reflection;
using System.Text.Json;
using ElasticMem.Core.Research;
namespace ElasticMem.App.Research;
internal sealed record ResearchOptions(int[] Pids,int Seconds=30,int SnapshotMiB=16,int Records=100000,int Iterations=100,int PoolMiB=32,int Adapter=0,string Scratch="",bool Negative=false,string Trace="",string Metadata="",double EvictionUs=10,double RecoveryUs=100,double MaxFaultUs=100,IReadOnlyDictionary<int,long>? ExpectedStarts=null,string SourceRun="",bool AllowTrim=false,int Epochs=12);
internal static class ResearchJobs
{
    public static string Root=>Path.Combine(AppSettings.Dir,"research-runs");
    public static string Commit=>Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(x=>x.Key=="CodeCommit")?.Value??"unknown-build";
    public static string Execute(string mode,ResearchOptions options,IProgress<string>? progress,CancellationToken ct,string? root=null)
    {
        using var run=new ResearchRun(root??Root,mode,Updater.Current.ToString(),Commit,options);
        try
        {
            string exe=Environment.ProcessPath!;run.Metadata["executable_sha256"]=ResearchRun.Hash(exe);
            run.Metadata["source_dirty_at_build"]=Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(x=>x.Key=="SourceDirty")?.Value??"unknown";
            run.Metadata["build_layout"]=Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(x=>x.Key=="BuildLayout")?.Value??"unknown";
            run.Metadata["external_binary_inventory"]=Directory.EnumerateFiles(AppContext.BaseDirectory,"*",SearchOption.TopDirectoryOnly).Where(p=>Path.GetExtension(p) is ".dll" or ".exe").Select(p=>new{name=Path.GetFileName(p),sha256=ResearchRun.Hash(p),bytes=new FileInfo(p).Length}).ToArray();
            progress?.Report("Прогон: "+run.DirectoryPath);object result;string status;
            switch(mode)
            {
                case "environment":result=WindowsEnvironment.Read(run);status="environment_probe_only";break;
                case "selftest":result=ResearchSelfTest.Run();status="selftests_passed";break;
                case "g0-sample":result=WindowsCapture.Sample(options.Pids,options.Seconds,run,progress,ct);status="sampled_observation_not_full_trace";break;
                case "g0-model":
                    if(new FileInfo(options.Trace).Length>128L<<20)throw new ArgumentException("Trace cap is 128 MiB for this build");
                    var trace=run.CopyInput(options.Trace,"trace.csv");var meta=run.CopyInput(options.Metadata,"metadata.json");
                    result=GapModel.Run(trace,meta,options.EvictionUs,options.RecoveryUs,options.MaxFaultUs,new[]{0d,.1,1,10,100},ct);status=System.Text.Json.JsonSerializer.SerializeToElement(result).GetProperty("status").GetString()!;break;
                case "g1":
                    if(options.Pids.Length==0||options.Pids.Length>4||options.Pids.Length*options.SnapshotMiB>128)throw new ArgumentException("Select 1..4 PIDs; total snapshot cap <=128 MiB");
                    var snapshots=options.Pids.Select(pid=>{progress?.Report("G1 снимок PID "+pid);return WindowsCapture.Capture(pid,options.SnapshotMiB,run,ct,options.ExpectedStarts==null?null:options.ExpectedStarts[pid]);}).ToArray();
                    var dedup=NormalizedDedup.Run(snapshots,run.Log,ct);string cold=run.FilePath("cold-store.emd");NormalizedColdStore.Write(cold,snapshots,ct);var restored=NormalizedColdStore.Restore(cold);
                    for(int p=0;p<snapshots.Length;p++)for(int i=0;i<snapshots[p].Pages.Length;i++)if(restored[p].Pages[i].Address!=snapshots[p].Pages[i].Address||!restored[p].Pages[i].Bytes.AsSpan().SequenceEqual(snapshots[p].Pages[i].Bytes))throw new InvalidDataException("Serialized cold store restore mismatch");
                    if(new FileInfo(cold).Length!=dedup.NormalizedBrotliBytes)throw new InvalidDataException("Serialized footprint model mismatch");
                    string baseline=run.FilePath("brotli-only.erb"),rawDedup=run.FilePath("raw-dedup.erb");
                    RawColdStore.Write(baseline,snapshots,false,ct);RawColdStore.Write(rawDedup,snapshots,true,ct);
                    void VerifyRaw(string path){var decoded=RawColdStore.Restore(path);for(int p=0;p<snapshots.Length;p++){if(decoded[p].Pages.Length!=snapshots[p].Pages.Length)throw new InvalidDataException("Raw page count mismatch");for(int i=0;i<snapshots[p].Pages.Length;i++)if(decoded[p].Pages[i].Address!=snapshots[p].Pages[i].Address||!decoded[p].Pages[i].Bytes.AsSpan().SequenceEqual(snapshots[p].Pages[i].Bytes))throw new InvalidDataException("Raw byte restore mismatch");}}
                    VerifyRaw(baseline);VerifyRaw(rawDedup);
                    if(new FileInfo(baseline).Length!=dedup.BrotliOnlyBytes||new FileInfo(rawDedup).Length!=dedup.RawBrotliBytes)throw new InvalidDataException("Raw footprint mismatch");
                    result=new{schema="elasticmem.g1.resident.v2",capture_policy="pre_and_post_resident_MEM_PRIVATE_not_shareable",normalization="ordinal_region_closed_reference_only",metrics=dedup,raw_page_analysis=RawPageAnalysis.Run(snapshots),residency=snapshots.Select(s=>System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(File.ReadAllText(run.FilePath($"residency-summary-{s.Pid}.json")))).ToArray(),actual_cold_store_bytes=new FileInfo(cold).Length,actual_brotli_only_bytes=new FileInfo(baseline).Length,actual_raw_dedup_brotli_bytes=new FileInfo(rawDedup).Length,serialized_restore_verified=true,all_three_serialized_formats_restore_verified=true};status="resident_private_offline_roundtrip_checked";break;
                case "g0-calibration":result=EpochCalibration.Run(run,ct);status="controlled_own_child_workload_only";break;
                case "g4":
                    var codecInput=SnapshotInputs.Load(options.SourceRun,run,ct);result=CodecBench.Run(codecInput,run.DirectoryPath,s=>progress?.Report("Г4: "+s),ct);status="offline_codec_comparison_not_live_reclaim";break;
                case "g0-epochs":
                    if(!options.AllowTrim)throw new ArgumentException("Explicit trim consent required");result=EpochResearch.Run(SnapshotInputs.Load(options.SourceRun,run,ct),true,options.Epochs,run,progress,ct);status="perturbing_epoch_proxy_not_exact_trace";break;
                case "g3-dma":result=GpuResearch.RunDma(run,options.Adapter,options.Iterations,options.PoolMiB,progress,ct);status="dma_microbenchmark_not_pager";break;
                case "g2":result=PackedAgentStore.Benchmark(options.Records,ct);status="own_schema_roundtrip_checked";break;
                case "gpu-probe":result=GpuResearch.Probe();status="capability_probe_only";break;
                case "g3":result=GpuResearch.Run(run,options.Scratch,options.Iterations,options.PoolMiB,options.Adapter,options.Negative,progress,ct);status="measured_microbenchmark_only";break;
                default:throw new ArgumentException("Unknown research mode: "+mode);
            }
            ct.ThrowIfCancellationRequested();run.WriteJson("result.json",result);run.Seal(status,"See result.json, raw files, parameters and limitations. No new transparent memory optimizer enabled.");ResearchRun.Verify(run.DirectoryPath);
        }
        catch(OperationCanceledException ex){run.Seal("cancelled_partial_not_result","Cancelled. Partial data preserved.",ex);}
        catch(NotSupportedException ex){run.Seal("unsupported_no_measurement",ex.Message,ex);}
        catch(Exception ex){if(run.IsSealed)run.Invalidate(ex);else run.Seal("failed_no_valid_result",ex.Message,ex);}
        return run.DirectoryPath;
    }
}
