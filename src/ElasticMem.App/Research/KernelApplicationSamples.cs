using System.Diagnostics;
using System.Globalization;
using ElasticMem.Core.Research;
using ElasticMem.DriverClient;
namespace ElasticMem.App.Research;
internal static class KernelApplicationSamples
{
    sealed class Adapter : ISamplePageStore
    {
        readonly KernelPageStore store;
        public Adapter(bool compressed)=>store=new(compressed?KernelPageStore.StoreMode.Xpress:KernelPageStore.StoreMode.Raw);
        public void Put(uint key,byte[] page)=>store.Put(key,page);
        public byte[] Get(uint key)=>store.Get(key);
        public void Reset()=>store.Reset();
        public SampleStoreStats Stats(){var s=store.Stats();return new(s.Pages,s.LogicalBytes,s.PayloadBytes,s.SessionAllocationBytes,s.GlobalAllocationBytes,s.CompressedPages,s.RawPages);}
        public void Dispose()=>store.Dispose();
    }
    public static object Run(ProcessSnapshot snapshot,ResearchRun run,IProgress<string>? progress,CancellationToken ct)
    {
        var pages=snapshot.Pages.Select(p=>p.Bytes).ToArray();
        var rows=PageStoreSampleBench.Run(pages,c=>new Adapter(c),s=>progress?.Report("VM kernel sample: "+s),ct);
        WriteCsv(run,rows);
        return new{schema="elasticmem.kernel.real_app_sample.v1",status="copied_sample_kernel_roundtrip_verified_not_live_reclaim",
            input_pages=pages.Length,backend="KMDF IOCTL RAW/XPRESS, not zstd",rows,
            same_snapshot_for_both_modes=true,full_bytes_verified=true,foreign_original_pages_released=false,
            physical_ram_saved_bytes=(long?)null,application_speedup=(double?)null,
            limitations=new[]{"Explicit copied samples only; target continues using original pages; paged pool adds allocations",
                "Requested pool allocations are not unique physical RAM, system commit or full pipeline footprint",
                "Sequential RAW then XPRESS; one PUT pass, first GET plus 3 warmed passes, not randomized application A/B",
                "PUT/GET include managed buffers, IOCTL and driver codec/copy, but exclude equality checks and buffer wiping",
                "p99 for bounded samples is descriptive, not a production tail guarantee; no pagefault interception"}};
    }
    public static object RandomControl(ResearchRun run,CancellationToken ct)
    {
        // Public deterministic bytes; a negative control, never substitute for an unavailable application.
        var random=new Random(25001);var pages=Enumerable.Range(0,64).Select(_=>{byte[] b=new byte[4096];random.NextBytes(b);return b;}).ToArray();
        try{var rows=PageStoreSampleBench.Run(pages,c=>new Adapter(c),ct:ct);WriteCsv(run,rows);return new{schema="elasticmem.kernel.random_control.v1",seed=25001,synthetic=true,rows,physical_ram_saved_bytes=(long?)null};}
        finally{foreach(var page in pages)Array.Clear(page);}
    }
    static void WriteCsv(ResearchRun run,SampleStoreRow[] rows)
    {
        using var w=new StreamWriter(run.FilePath("kernel-samples.csv"));
        w.WriteLine("mode,pages,logical_bytes,payload_bytes,session_requested_bytes,global_requested_bytes,compressed_pages,raw_pages,verified_reads,full_bytes_verified,put_p50_us,put_p95_us,put_p99_us,first_get_p50_us,warm_get_p50_us,warm_get_p95_us,warm_get_p99_us,raw_to_payload_ratio,session_requested_reduction_vs_raw");
        foreach(var r in rows)w.WriteLine(string.Join(",",new object?[]{r.Mode,r.Pages,r.Full.LogicalBytes,r.Full.PayloadBytes,r.Full.SessionAllocationBytes,r.Full.GlobalAllocationBytes,r.Full.CompressedPages,r.Full.RawPages,r.VerifiedReads,r.FullBytesVerified,r.PutP50Us,r.PutP95Us,r.PutP99Us,r.FirstGetP50Us,r.WarmGetP50Us,r.WarmGetP95Us,r.WarmGetP99Us,r.RawToPayloadRatio,r.SessionAllocationReductionVsRaw}.Select(x=>x is IFormattable f?f.ToString(null,CultureInfo.InvariantCulture):x?.ToString()??"")));
    }
}
