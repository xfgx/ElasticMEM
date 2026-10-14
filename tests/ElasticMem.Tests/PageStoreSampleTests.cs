using ElasticMem.Core.Research;
static class PageStoreSampleTests
{
    sealed class Fake : ISamplePageStore
    {
        readonly bool compressed, corrupt, badReset;readonly Action close;readonly Dictionary<uint,byte[]> data=new();bool filled;
        public Fake(bool compressed,Action close,bool corrupt=false,bool badReset=false){this.compressed=compressed;this.close=close;this.corrupt=corrupt;this.badReset=badReset;}
        public void Put(uint key,byte[] page){data[key]=(byte[])page.Clone();filled=true;}
        public byte[] Get(uint key){var b=(byte[])data[key].Clone();if(corrupt)b[0]^=1;return b;}
        public void Reset(){foreach(var b in data.Values)Array.Clear(b);data.Clear();}
        public SampleStoreStats Stats(){ulong n=(ulong)data.Count;return new((uint)n,n*4096,n*(compressed?64UL:4096UL),32768+n*(compressed?96UL:4128UL)+(badReset&&filled&&n==0?8UL:0),40000+n*4096,compressed?n:0,compressed?0:n);}
        public void Dispose(){Reset();close();}
    }
    public static int Run()
    {
        int fails=0,checks=0;void Check(bool ok,string label){checks++;if(!ok){fails++;Console.WriteLine("FAIL sample bench: "+label);}}
        byte[][] pages=Enumerable.Range(0,4).Select(i=>Enumerable.Repeat((byte)i,4096).ToArray()).ToArray();int closed=0;
        var rows=PageStoreSampleBench.Run(pages,c=>new Fake(c,()=>closed++));
        Check(rows.Length==2&&closed==2,"both sessions disposed");
        Check(rows.All(x=>x.FullBytesVerified&&x.VerifiedReads==16),"all four read passes checked");
        Check(rows[1].SessionAllocationReductionVsRaw>0&&rows[1].RawToPayloadRatio==64,"requested allocation metrics");
        Check(rows.All(x=>x.AfterReset.Pages==0&&x.AfterReset.SessionAllocationBytes==x.Before.SessionAllocationBytes),"reset baseline");
        Check(pages[1].All(x=>x==1),"input not modified");
        bool rejected=false;try{PageStoreSampleBench.Run(new[]{new byte[12]},c=>new Fake(c,()=>{}));}catch(ArgumentException){rejected=true;}Check(rejected,"shape rejected");
        rejected=false;try{PageStoreSampleBench.Run(Array.Empty<byte[]>(),c=>new Fake(c,()=>{}));}catch(ArgumentException){rejected=true;}Check(rejected,"empty rejected");
        rejected=false;try{PageStoreSampleBench.Run(Enumerable.Repeat(pages[0],2049).ToArray(),c=>new Fake(c,()=>{}));}catch(ArgumentException){rejected=true;}Check(rejected,"cap rejected");
        closed=0;rejected=false;try{PageStoreSampleBench.Run(pages,c=>new Fake(c,()=>closed++,corrupt:true));}catch(InvalidDataException){rejected=true;}Check(rejected&&closed==1,"corrupt rejected and disposed");
        closed=0;rejected=false;try{PageStoreSampleBench.Run(pages,c=>new Fake(c,()=>closed++,badReset:true));}catch(InvalidDataException){rejected=true;}Check(rejected&&closed==1,"reset allocation mismatch rejected");
        using var cancellation=new CancellationTokenSource();closed=0;rejected=false;
        try{PageStoreSampleBench.Run(pages,c=>{cancellation.Cancel();return new Fake(c,()=>closed++);},ct:cancellation.Token);}catch(OperationCanceledException){rejected=true;}Check(rejected&&closed==1,"mid-run cancellation disposes");
        var json=System.Text.Json.JsonSerializer.SerializeToElement(rows,ResearchRun.Json);
        Check(json[0].TryGetProperty("put_p99_us",out _)&&json[0].TryGetProperty("warm_get_p99_us",out _),"UI JSON field names");
        Check(json[1].GetProperty("full").GetProperty("session_allocation_bytes").GetUInt64()>0,"full requested allocation field");
        string root=Path.Combine(Path.GetTempPath(),"elasticmem-sample-export-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string runs=Path.Combine(root,"runs");
            using(var run=new ResearchRun(runs,"sample-test","test","test-double",new{physical_ram_saved_bytes=(long?)null}))
            {
                File.WriteAllText(run.FilePath("kernel-samples.csv"),"mode,pages\nRAW,4\n");
                File.WriteAllText(run.FilePath("application-compression-summary.csv"),"application,stored_bytes\nmsedge,17000\n");
                File.WriteAllText(run.FilePath("snapshots/1/pages.bin"),"DO_NOT_EXPORT_PRIVATE_PAGES");
                File.WriteAllText(run.FilePath("sample.ecb"),"DO_NOT_EXPORT_PRIVATE_CONTAINER");
                run.Seal("test_double_only","Not a native app test.");
            }
            string zip=ResearchRun.ExportLogs(runs,Path.Combine(root,"logs.zip"),false);
            using var archive=System.IO.Compression.ZipFile.OpenRead(zip);var names=archive.Entries.Select(e=>e.FullName).ToArray();
            Check(names.Any(n=>n.EndsWith("/kernel-samples.csv"))&&names.Any(n=>n.EndsWith("/application-compression-summary.csv")),"new CSVs exported");
            Check(!names.Any(n=>n.EndsWith(".bin")||n.EndsWith(".ecb")||n.Contains("/snapshots/")),"private samples/containers excluded");
            using var exportReader=new StreamReader(archive.GetEntry("EXPORT.json")!.Open());using var export=System.Text.Json.JsonDocument.Parse(exportReader.ReadToEnd());
            Check(!export.RootElement.GetProperty("sensitive_data_included").GetBoolean(),"export privacy flag");
        }
        finally{Directory.Delete(root,true);}
        Console.WriteLine($"SAMPLE BENCH {checks} checks; backend=test double, NOT native XPRESS");return fails;
    }
}
