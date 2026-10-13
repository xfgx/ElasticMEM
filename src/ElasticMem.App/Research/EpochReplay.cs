using System.Text.Json;
using ElasticMem.Core.Research;
namespace ElasticMem.App.Research;
internal static class EpochReplay
{
 public static string LastSource()
 {
  string root=Path.Combine(AppSettings.Dir,"research-batches");if(!Directory.Exists(root))throw new ArgumentException("Нет локальных партий; сначала выполнить Г0 с согласием trim");
  foreach(string batch in Directory.GetDirectories(root).OrderDescending())
  {
   string runs=Path.Combine(batch,"runs");if(!Directory.Exists(runs))continue;
   foreach(string dir in Directory.GetDirectories(runs,"*_auto-summary_*").OrderDescending())
   {try{using var m=JsonDocument.Parse(File.ReadAllText(Path.Combine(dir,"manifest.json")));if(m.RootElement.GetProperty("status").GetString() is "completed_with_skips" or "completed" or "completed_with_failures" or "completed_checks_not_effectiveness_claim")return dir;}catch(Exception ex)when(ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException){/* Incomplete candidate is skipped,selected source integrity is still fail-closed in Run. */}}
  }
  throw new ArgumentException("Нет завершённой локальной партии с картами эпох");
 }
 public static object Run(string source,ResearchRun run,IProgress<string>? progress,CancellationToken ct)
 {
  if(string.IsNullOrWhiteSpace(source))source=LastSource();ResearchRun.Verify(source);using var m=JsonDocument.Parse(File.ReadAllText(Path.Combine(source,"manifest.json")));string mode=m.RootElement.GetProperty("mode").GetString()!;var folders=new List<string>();
  if(mode=="g0-epochs")folders.Add(source);
  else if(mode=="auto-summary")
  {
   using var result=JsonDocument.Parse(File.ReadAllText(Path.Combine(source,"result.json")));
   foreach(var step in result.RootElement.GetProperty("steps").EnumerateArray())if(step.GetProperty("check").GetString()=="g0-epochs"&&step.GetProperty("status").GetString()=="perturbing_epoch_proxy_not_exact_trace")
   {string name=step.GetProperty("folder").GetString()!;if(name!=Path.GetFileName(name)||name.Contains('\\')||name is "." or "..")throw new InvalidDataException("Unsafe source folder");folders.Add(Path.Combine(Path.GetDirectoryName(source)!,name));}
  }
  else throw new ArgumentException("Source must be successful G0 epochs or completed automatic summary,not logs ZIP");
  if(folders.Count is <1 or >4||folders.Distinct().Count()!=folders.Count)throw new ArgumentException("Need1..4 independent epoch sources");
  var results=new List<object>();var ids=new HashSet<(int,long)>();
  foreach(string folder in folders)
  {
   ct.ThrowIfCancellationRequested();ResearchRun.Verify(folder);using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"manifest.json")));
   if(manifest.RootElement.GetProperty("mode").GetString()!="g0-epochs"||manifest.RootElement.GetProperty("status").GetString()!="perturbing_epoch_proxy_not_exact_trace")throw new ArgumentException("Fully successful epoch source required");
   int planned=manifest.RootElement.GetProperty("parameters").GetProperty("epochs").GetInt32();using var old=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"result.json")));var roster=old.RootElement.GetProperty("processes").EnumerateArray().ToArray();var files=Directory.GetFiles(Path.Combine(folder,"inputs"),"epochs-*.eet");if(roster.Length!=files.Length||roster.Length is <1 or >4)throw new InvalidDataException("Epoch roster");
   var seen=new HashSet<int>();
   foreach(string file in files.Order())
   {
    progress?.Report("Офлайн: "+Path.GetFileName(file));long packedBytes=new FileInfo(file).Length;ulong free=Native.MemoryStatus().ullAvailPhys;if(packedBytes>80L<<20||free<512UL<<20||(ulong)(packedBytes*16+(512L<<20))>free/2)throw new NotSupportedException("Offline history memory guard: stop rather than induce paging pressure");var tr=EpochTraceFile.Read(run.CopyInput(file,Path.GetFileName(file)),planned);
    if(!seen.Add(tr.Pid)||!ids.Add((tr.Pid,tr.Start)))throw new InvalidDataException("Duplicate epoch source identity");var proc=roster.Single(p=>p.GetProperty("pid").GetInt32()==tr.Pid);
    string timingFile=Path.Combine(folder,"inputs",$"epochs-{tr.Pid}.json");using var times=JsonDocument.Parse(File.ReadAllText(run.CopyInput(timingFile,Path.GetFileName(timingFile))));if(times.RootElement.GetProperty("pid").GetInt32()!=tr.Pid||times.RootElement.GetProperty("start_ticks").GetInt64()!=tr.Start)throw new InvalidDataException("Duration identity");
    double[] durations=times.RootElement.GetProperty("measured_epoch_seconds").EnumerateArray().Select(x=>x.GetDouble()).ToArray();if(durations.Length!=planned||durations.Any(x=>!double.IsFinite(x)||x<=0))throw new InvalidDataException("Duration bounds");
    object diagnostic=EpochDiagnostics.Run(tr.Activity,tr.KnownRows,tr.InitialResident,durations,ct);
    using var inv=JsonDocument.Parse(File.ReadAllText(run.CopyInput(Path.Combine(folder,"inputs",$"inventory-{tr.Pid}.json"),$"inventory-{tr.Pid}.json")));if(inv.RootElement.GetProperty("pid").GetInt32()!=tr.Pid||inv.RootElement.GetProperty("start_ticks").GetInt64()!=tr.Start)throw new InvalidDataException("Snapshot identity");
    string rawFile=Path.Combine(folder,"inputs",$"pages-{tr.Pid}.bin");if(new FileInfo(rawFile).Length>64L<<20)throw new InvalidDataException("Snapshot cap");byte[] raw=File.ReadAllBytes(run.CopyInput(rawFile,Path.GetFileName(rawFile)));var items=inv.RootElement.GetProperty("pages").EnumerateArray().ToArray();if(items.Length*4096L!=raw.Length||items.Where((x,i)=>x.GetProperty("file_offset").GetInt64()!=i*4096L).Any())throw new InvalidDataException("Snapshot offsets");
    var index=tr.Addresses.Select((a,i)=>(a,i)).ToDictionary(x=>x.a,x=>x.i);int split=planned/2;
    int[] matched=Enumerable.Range(0,items.Length).Where(i=>index.TryGetValue(items[i].GetProperty("address").GetUInt64(),out int p)&&tr.KnownRows.Take(split).All(row=>row[p])&&tr.Activity.Take(split).Any(row=>row[p])).ToArray();
    // Bounded address-spread cohort selected ONLY from training availability/activity.
    int count=Math.Min(1024,matched.Length);int[] selected=Enumerable.Range(0,count).Select(i=>matched[i*matched.Length/count]).ToArray();
    object causal;
    if(count<16)causal=new{status="skipped_fewer_than16_train_observed_snapshot_pages"};
    else{var addresses=selected.Select(i=>items[i].GetProperty("address").GetUInt64()).ToArray();var cohort=new CodecInput(tr.Pid,tr.Start,addresses,selected.Select(i=>raw.AsSpan(i*4096,4096).ToArray()).ToArray());causal=CausalGrouping.Run(cohort,tr.Activity.Select(row=>addresses.Select(a=>row[index[a]]).ToArray()).ToArray(),tr.KnownRows.Select(row=>addresses.Select(a=>row[index[a]]).ToArray()).ToArray(),ct);}
    string healthFile=Path.Combine(folder,$"epoch-health-{tr.Pid}.json");JsonElement? health=null;
    if(File.Exists(healthFile)){using var hd=JsonDocument.Parse(File.ReadAllText(healthFile));if(hd.RootElement.GetProperty("schema").GetString()!="elasticmem.epoch.health.v1"||hd.RootElement.GetProperty("pid").GetInt32()!=tr.Pid||hd.RootElement.GetProperty("epochs").GetInt32()!=planned)throw new InvalidDataException("Health identity/epoch mismatch");health=hd.RootElement.Clone();}
    results.Add(new{pid=tr.Pid,planned_epochs=planned,baseline_residency_available=tr.InitialResident!=null,causal_cohort_pages=count,trace_health_available=health.HasValue,trace_health=health,diagnostic,causal});
   }
  }
  return new{schema="elasticmem.epochs.replay.v1",status="offline_epoch_replay_not_physical_ram",processes=results,settings_changed=false,new_process_reads=false,foreign_trim=false,limitations=new[]{"Verified local EET/snapshot replay; private inputs stay local,logs export contains only aggregates","Old EETv2 has NO initial residency; never-observed pages excluded and first-return activation,not retroactive baseline recovery","No true Belady,GC attribution,private-commit minus WS subtraction or additionalRAM claim","Sequential process traces must not be summed as simultaneous desktop physical saving","Causal cohort<=1024 TRAIN-observed/known address-spread snapshot pages,selection bias reported","No confirmed decommit events in old trace; unknown is excluded/reset,NOT death; no live churn implementation"}};
 }
}
