namespace ElasticMem.Core.Research;
public static class CalibrationStatistics
{
    public static object PairedInterval(double[] baseline,double[] measured)
    {
        if(baseline.Length!=measured.Length||baseline.Length<3||baseline.Any(x=>x<=0||!double.IsFinite(x))||measured.Any(x=>x<=0||!double.IsFinite(x)))
            throw new ArgumentException("Paired throughput observations");
        double[] changes=baseline.Zip(measured,(b,m)=>100*(m/b-1)).ToArray();
        var rng=new Random(17);var means=new double[5000];
        for(int i=0;i<means.Length;i++){double sum=0;for(int j=0;j<changes.Length;j++)sum+=changes[rng.Next(changes.Length)];means[i]=sum/changes.Length;}
        return new{rounds=changes.Length,paired_changes_pct=changes,mean_paired_change_pct=changes.Average(),
            bootstrap_percentile_95_low=CodecBench.Quantile(means,.025),bootstrap_percentile_95_high=CodecBench.Quantile(means,.975),
            bootstrap_seed=17,bootstrap_resamples=means.Length,
            interpretation="Descriptive paired-round bootstrap; dependent rounds,order drift and uncontrolled OS activity limit causal interpretation; not a zero-overhead guarantee"};
    }
}