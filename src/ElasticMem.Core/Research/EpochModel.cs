namespace ElasticMem.Core.Research;
public static class EpochModel
{
    public static object Run(bool[][] activity,double epochSeconds,double[] missCostsUs,double budgetUsPerSecond,double[]? epochDurationsSeconds=null,CancellationToken ct=default,bool[]? initialResident=null)
    {
        if(activity.Length<2||activity[0].Length==0||activity.Any(a=>a.Length!=activity[0].Length)||epochSeconds<=0||!double.IsFinite(epochSeconds)||missCostsUs.Any(c=>c<0||!double.IsFinite(c))||budgetUsPerSecond<0||!double.IsFinite(budgetUsPerSecond)||missCostsUs.Length==0)throw new ArgumentException("Epoch model shape/cost");
        double[] durations=epochDurationsSeconds??Enumerable.Repeat(epochSeconds,activity.Length).ToArray();if(durations.Length!=activity.Length||durations.Any(d=>d<=0||!double.IsFinite(d)))throw new ArgumentException("Epoch durations");
        if(initialResident!=null&&initialResident.Length!=activity[0].Length)throw new ArgumentException("Initial residency shape");
        var curves=new List<object>();var points=new List<(int T,double V,double R)>();
        foreach(int threshold in new[]{1,2,4,8,16,32,64,128,256})
        {
            ct.ThrowIfCancellationRequested();
            int n=activity[0].Length;var idle=new int[n];var away=new bool[n];var present=initialResident==null?Enumerable.Repeat(true,n).ToArray():(bool[])initialResident.Clone();var bursts=new List<double>();long faults=0;double outsideSeconds=0;int epochIndex=0;
            foreach(var epoch in activity){ct.ThrowIfCancellationRequested();long epochFaults=0;for(int p=0;p<n;p++){if(epoch[p]){if(away[p]){faults++;epochFaults++;}present[p]=true;away[p]=false;idle[p]=0;}else if(present[p]){idle[p]++;if(idle[p]>=threshold)away[p]=true;}if(away[p])outsideSeconds+=durations[epochIndex];}bursts.Add(epochFaults);epochIndex++;}
            double v=outsideSeconds*4096/durations.Sum(),r=faults/durations.Sum();points.Add((threshold,v,r));curves.Add(new{idle_epochs=threshold,average_end_epoch_offloaded_bytes=v,modeled_reacquisitions_per_second=r,modeled_misses_per_epoch=bursts,misses_per_epoch_p99=CodecBench.Quantile(bursts,.99),misses_per_epoch_max=bursts.Max(),threshold_epochs_available=activity.Length,tail_window_epochs=Math.Max(0,activity.Length-threshold),cost_scenarios=missCostsUs.Select(c=>new{miss_cost_us=c,added_time_us_per_second=r*c,within_budget=r*c<=budgetUsPerSecond})});
        }
        return new{schema="elasticmem.g0.epoch_model.v2",status="approximate_reacquisition_proxy_not_exact_trace",pages=activity[0].Length,epochs=activity.Length,nominal_epoch_seconds=epochSeconds,measured_epoch_seconds=durations,budget_us_per_second=budgetUsPerSecond,curves,scenario_best=missCostsUs.Select(c=>new{miss_cost_us=c,maximum_among_tested_thresholds_bytes=points.Where(p=>p.R*c<=budgetUsPerSecond).Select(p=>p.V).DefaultIfEmpty(0).Max()}),limitations=new[]{"Input is observed reacquisition after trim,not all page accesses; all pages must have known states at every sampled epoch","Caller initial state: null is legacy all-present; explicit false activates at first observed return; replay labels cohort; not physical frames","No intra-epoch ordering/repeated references,therefore NOT Belady or true ceiling","Cost values are user-selected scenarios,not measured NVMe/Xpress/VRAM latency","Only sampled virtual pages,not unique physical frames or whole desktop memory; no sum across sharing"}};
    }
}
