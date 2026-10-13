namespace ElasticMem.Core.Research;
public static class EpochDiagnostics
{
 public static object Run(bool[][] activity,bool[][] known,bool[]? initial,double[] durations,CancellationToken ct)
 {
  if(activity.Length<8||known.Length!=activity.Length||durations.Length!=activity.Length||activity.Any(x=>x.Length!=activity[0].Length)||known.Any(x=>x.Length!=activity[0].Length)||initial!=null&&initial.Length!=activity[0].Length)throw new ArgumentException("Diagnostics shape");
  int n=activity[0].Length;bool Checkpoint(int p){if((p&1023)==0)ct.ThrowIfCancellationRequested();return true;}
  var all=Enumerable.Range(0,n).Where(p=>Checkpoint(p)&&known.All(row=>row[p])).ToArray();
  var ever=all.Where(p=>Checkpoint(p)&&activity.Any(row=>row[p])).ToArray();var primary=initial==null?ever:all.Where(p=>Checkpoint(p)&&initial[p]).ToArray();
  object Model(int[] cohort,bool firstObservation)=>cohort.Length==0?new{status="no_eligible_pages"}:EpochModel.Run(activity.Select(a=>{ct.ThrowIfCancellationRequested();return cohort.Select(p=>a[p]).ToArray();}).ToArray(),1,new[]{10d,20,40,100},10000,durations,ct,firstObservation?new bool[cohort.Length]:null);
  double[] counts=activity.Select(row=>{ct.ThrowIfCancellationRequested();return (double)all.Count(p=>row[p]);}).ToArray();double cut=CodecBench.Quantile(counts,.95);bool[] peak=counts.Select(x=>x>cut).ToArray();int peakOnly=all.Count(p=>Checkpoint(p)&&Enumerable.Range(0,activity.Length).Any(t=>peak[t]&&activity[t][p])&&!Enumerable.Range(0,activity.Length).Any(t=>!peak[t]&&activity[t][p]));
  double mean=counts.Average(),variance=counts.Sum(x=>(x-mean)*(x-mean));double? strongest=null;int? lag=null;
  if(variance>0)for(int d=2;d<=Math.Min(128,counts.Length/4);d++){ct.ThrowIfCancellationRequested();double corr=Enumerable.Range(d,counts.Length-d).Sum(t=>(counts[t]-mean)*(counts[t-d]-mean))/variance;if(strongest==null||corr>strongest){strongest=corr;lag=d;}}
  return new{schema="elasticmem.epochs.diagnostics.v1",initial_residency_available=initial!=null,all_known_pages=all.Length,ever_observed_pages=ever.Length,initially_resident_all_known_pages=initial==null?(int?)null:primary.Length,never_observed_nonresident_pages=initial==null?(int?)null:Enumerable.Range(0,n).Count(p=>Checkpoint(p)&&!initial[p]&&!activity.Any(row=>row[p])),primary_cohort=initial==null?"ever_observed_activated_at_first_return_not_baseline":"initially_resident_all_known",model=Model(primary,initial==null),newly_observed_model=initial==null?null:Model(ever.Where(p=>!initial[p]).ToArray(),true),observed_return_pages_per_epoch=counts,burst_p99_pages=CodecBench.Quantile(counts,.99),burst_max_pages=counts.Max(),peak_rule="strictly above within-run p95; descriptive,not GC detection",peak_epochs=peak.Count(x=>x),pages_observed_only_in_peak_epochs=peakOnly,strongest_autocorrelation_lag_epochs=lag,strongest_autocorrelation=strongest,gc_attribution="unavailable_no_GC_events_or_heap_classification",belady="unavailable_no_intra_epoch_access_order",incremental_physical_ram="unavailable_no_counterfactual_physical_frame_or_store_measurement"};
 }
}
