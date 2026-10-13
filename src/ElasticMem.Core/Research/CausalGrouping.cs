using System.Numerics;
using System.Diagnostics;
namespace ElasticMem.Core.Research;
public sealed record CausalEvent(int Epoch,int[] Pages,int Useful);
public static class CausalGrouping
{
 public static (List<CausalEvent> Events,int Excluded,int FormationCount) Simulate(ulong[] addresses,bool[][] activity,bool[][] known,int size,int threshold,string policy,CancellationToken ct=default)
 {
  int n=addresses.Length,epochs=activity.Length,split=epochs/2;
  if(n==0||n>4096||epochs<8||known.Length!=epochs||activity.Any(a=>a.Length!=n)||known.Any(a=>a.Length!=n)||size is not(1 or 4 or 8 or 16)||threshold<1||threshold>256||policy is not("adjacency" or "coaccess"))throw new ArgumentException("Causal shape/options");
  // Prefix only, frozen past history; no suffix samples enter partner ranking.
  ulong[] signatures=new ulong[n];for(int p=0;p<n;p++)for(int t=0;t<Math.Min(64,split);t++){int at=t*split/Math.Min(64,split);if(known[at][p]&&activity[at][p])signatures[p]|=1UL<<t;}
  int[] order=Enumerable.Range(0,n).OrderBy(p=>addresses[p]).ToArray(),idle=new int[n],owner=Enumerable.Repeat(-1,n).ToArray();var blocks=new List<int[]>();var events=new List<CausalEvent>();int excluded=0,formed=0;
  for(int t=split;t<epochs;t++)
  {
   ct.ThrowIfCancellationRequested();
   if(known[t].Any(x=>!x)){excluded++;Array.Clear(idle);Array.Fill(owner,-1);blocks.Clear();continue;}
   var returning=order.Where(p=>activity[t][p]&&owner[p]>=0).Select(p=>owner[p]).Distinct().ToArray();
   foreach(int id in returning)
   {
    var group=blocks[id];int useful=group.Count(p=>activity[t][p]);events.Add(new(t,(int[])group.Clone(),useful));
    // No speculative future utility: count pages touched in SAME triggering epoch only.
    foreach(int p in group){owner[p]=-1;idle[p]=0;}
   }
   for(int p=0;p<n;p++)if(activity[t][p])idle[p]=0;else idle[p]++;
   foreach(int first in order)
   {
    if(owner[first]>=0||idle[first]<threshold)continue;
    ct.ThrowIfCancellationRequested();var candidates=order.Where(p=>p!=first&&owner[p]<0&&idle[p]>=threshold);
    if(policy=="coaccess")candidates=candidates.OrderBy(p=>BitOperations.PopCount(signatures[first]^signatures[p])).ThenBy(p=>addresses[p]);
    else candidates=candidates.OrderBy(p=>addresses[p]>=addresses[first]?addresses[p]-addresses[first]:addresses[first]-addresses[p]).ThenBy(p=>addresses[p]);
    var group=new[]{first}.Concat(candidates.Take(size-1)).ToArray();int id=blocks.Count;blocks.Add(group);foreach(int p in group)owner[p]=id;formed++;
   }
   // Retired block records are bounded by epochs*n; actual arrays reclaimed each time all blocks inactive.
   if(owner.All(x=>x<0))blocks.Clear();
  }
  return(events,excluded,formed);
 }
 public static object Run(CodecInput source,bool[][] activity,bool[][] known,CancellationToken ct)
 {
  if(source.Pages.Length!=source.Addresses.Length||source.Pages.Any(p=>p.Length!=4096))throw new ArgumentException("Snapshot shape");
  var rows=new List<object>();var adjacencyMeans=new Dictionary<(int,int),double?>();var adjacencyEvents=new Dictionary<(int,int),int>();using var codec=new PortablePageCodec("zstd1");int split=activity.Length/2;
  foreach(int threshold in new[]{1,2,4,8,16,32,64,128,256})foreach(int size in new[]{1,4,8,16})foreach(string policy in size==1?new[]{"adjacency"}:new[]{"adjacency","coaccess"})
  {
   ct.ThrowIfCancellationRequested();var sim=Simulate(source.Addresses,activity,known,size,threshold,policy,ct);long useful=sim.Events.Sum(e=>(long)e.Useful),decoded=sim.Events.Sum(e=>(long)e.Pages.Length);int measured=0;double cpu=0;long rawBytes=0,storedBytes=0;
   // First128 actual decode events: deterministic bounded CPU/size sample,NOT measured pager cost.
   foreach(var ev in sim.Events.Take(128))
   {
    ct.ThrowIfCancellationRequested();byte[] raw=ev.Pages.SelectMany(p=>source.Pages[p]).ToArray(),packed=codec.Encode(raw);bool fallback=packed.Length>=raw.Length;byte[] payload=fallback?raw:packed;
    byte[] restored=fallback?(byte[])payload.Clone():codec.Decode(payload,raw.Length);if(!restored.AsSpan().SequenceEqual(raw))throw new InvalidDataException("Causal sample restore");
    double[] timing=new double[3];for(int k=0;k<3;k++){long tick=Stopwatch.GetTimestamp();restored=fallback?(byte[])payload.Clone():codec.Decode(payload,raw.Length);timing[k]=(Stopwatch.GetTimestamp()-tick)*1e6/Stopwatch.Frequency;}
    cpu+=timing.Order().ElementAt(1);rawBytes+=raw.Length;storedBytes+=((payload.Length+7)&~7)+16+24L*ev.Pages.Length;measured++;
   }
   long sampleUseful=sim.Events.Take(measured).Sum(e=>(long)e.Useful);double? mean=sim.Events.Count==0?null:(double)useful/sim.Events.Count;
   if(policy=="adjacency"){adjacencyMeans[(threshold,size)]=mean;adjacencyEvents[(threshold,size)]=sim.Events.Count;}
   bool? gate=policy!="coaccess"||sim.Excluded>0||sim.Events.Count<30||adjacencyEvents[(threshold,size)]<30||!mean.HasValue||!adjacencyMeans[(threshold,size)].HasValue?null:size==4?mean>=2.5&&mean>adjacencyMeans[(threshold,size)]:size==16?mean>=8&&mean>adjacencyMeans[(threshold,size)]:null;
   rows.Add(new{policy,idle_epochs=threshold,block_pages=size,test_epochs=activity.Length-split,excluded_common_unknown_epochs=sim.Excluded,coverage_fraction=(double)(activity.Length-split-sim.Excluded)/(activity.Length-split),block_formations=sim.FormationCount,decode_events=sim.Events.Count,useful_pages=useful,decoded_pages=decoded,mean_useful_pages_per_decode=mean,gate_same_epoch_only=gate,sampled_decode_events=measured,sampled_cpu_us=cpu,sampled_cpu_us_per_useful_page=sampleUseful==0?(double?)null:cpu/sampleUseful,sampled_raw_bytes=rawBytes,sampled_layout_bytes=storedBytes,sampled_layout_with_8pct_reserve_bytes=(long)Math.Ceiling(storedBytes/256d)*256+(long)Math.Ceiling(Math.Ceiling(storedBytes/256d)*256*.08/256)*256});
  }
  return new{schema="elasticmem.g5.causal.v1",pid=source.Pid,pages=source.Pages.Length,rows,limitations=new[]{"Causal cold partners at eviction epoch end,ranking frozen training prefix; no test suffix leakage","Strict same-trigger-epoch usefulness; future prefetch utility NOT credited,not original N=1 metric","Any unknown cohort cell drops epoch for ALL policies and clears residency/idle/block state; not decommit/deadslot,no positive gate with unknown","First128 actual event payloads measured,three warmed decode/copy trials median; snapshot stale,not renderer churn or total pager CPU","Sizes include bindings/header/padding/256B allocation/8percent reserve for sample,not live allocator footprint","Model initially in RAM at held-out start,threshold warmup included; no measured device/pagefile latency,cache capacity or GC attribution","One-page baseline uses identical trace/cohort; larger groups may be underfilled; event count minimum30 for descriptive utility gate","Real coeviction controller,online learning overhead,pending transfer,metadata,TDR/sleep/pagefile/RAMdisk not implemented"}};
 }
}
