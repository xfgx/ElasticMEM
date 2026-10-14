using System.Diagnostics;
namespace ElasticMem.Core.Research;

// The backend may be native or a test double. These counters are requested allocations, not RSS.
public sealed record SampleStoreStats(uint Pages, ulong LogicalBytes, ulong PayloadBytes,
    ulong SessionAllocationBytes, ulong GlobalAllocationBytes, ulong CompressedPages, ulong RawPages);
public interface ISamplePageStore : IDisposable
{
    void Put(uint key, byte[] page);
    byte[] Get(uint key);
    void Reset();
    SampleStoreStats Stats();
}
public sealed record SampleStoreRow(string Mode, int Pages, int VerifiedReads, bool FullBytesVerified,
    SampleStoreStats Before, SampleStoreStats Full, SampleStoreStats AfterReset,
    double PutP50Us, double PutP95Us, double PutP99Us,
    double FirstGetP50Us, double WarmGetP50Us, double WarmGetP95Us, double WarmGetP99Us,
    double RawToPayloadRatio, double? SessionAllocationReductionVsRaw);

public static class PageStoreSampleBench
{
    public static SampleStoreRow[] Run(IReadOnlyList<byte[]> pages,
        Func<bool,ISamplePageStore> factory, Action<string>? log=null, CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(pages); ArgumentNullException.ThrowIfNull(factory);
        if(pages.Count is <1 or >2048 || pages.Any(p=>p==null || p.Length!=4096))
            throw new ArgumentException("Need 1..2048 complete 4096-byte pages");
        var rows=new List<SampleStoreRow>();
        foreach(bool compressed in new[]{false,true})
        {
            ct.ThrowIfCancellationRequested(); string mode=compressed?"XPRESS":"RAW";
            log?.Invoke(mode+": same captured bytes, explicit copy only");
            using var store=factory(compressed); store.Reset(); var before=store.Stats();
            if(before.Pages!=0 || before.LogicalBytes!=0 || before.PayloadBytes!=0)
                throw new InvalidDataException("Fresh/reset sample store is not empty");
            var puts=new List<double>();var first=new List<double>();var warm=new List<double>();
            for(int i=0;i<pages.Count;i++)
            {
                ct.ThrowIfCancellationRequested();long t=Stopwatch.GetTimestamp();
                store.Put((uint)i,pages[i]); puts.Add(Us(t));
            }
            var full=store.Stats();ulong logical=(ulong)pages.Count*4096;
            if(full.Pages!=(uint)pages.Count || full.LogicalBytes!=logical || full.PayloadBytes==0 ||
               full.PayloadBytes>logical || full.CompressedPages+full.RawPages!=(ulong)pages.Count ||
               (!compressed && (full.PayloadBytes!=logical || full.CompressedPages!=0)))
                throw new InvalidDataException("Sample store counters inconsistent");
            int reads=0;
            // First full read plus three warmed reads; timings exclude equality checks and buffer wiping.
            for(int pass=0;pass<4;pass++)for(int i=0;i<pages.Count;i++)
            {
                ct.ThrowIfCancellationRequested();long t=Stopwatch.GetTimestamp();byte[] got=store.Get((uint)i);
                double us=Us(t);
                try
                {
                    if(!got.AsSpan().SequenceEqual(pages[i]))throw new InvalidDataException("Full-byte sample restore mismatch");
                    reads++; (pass==0?first:warm).Add(us);
                }
                finally{Array.Clear(got);}
            }
            store.Reset();var after=store.Stats();
            if(after.Pages!=0 || after.LogicalBytes!=0 || after.PayloadBytes!=0 ||
               after.SessionAllocationBytes!=before.SessionAllocationBytes)
                throw new InvalidDataException("Reset did not return session allocations to baseline");
            double? reduction=compressed?1-(double)full.SessionAllocationBytes/rows[0].Full.SessionAllocationBytes:null;
            rows.Add(new(mode,pages.Count,reads,true,before,full,after,
                Q(puts,.5),Q(puts,.95),Q(puts,.99),Q(first,.5),Q(warm,.5),Q(warm,.95),Q(warm,.99),
                (double)logical/full.PayloadBytes,reduction));
        }
        return rows.ToArray();
    }
    static double Us(long t)=>(Stopwatch.GetTimestamp()-t)*1e6/Stopwatch.Frequency;
    static double Q(List<double> x,double q)=>CodecBench.Quantile(x,q);
}
