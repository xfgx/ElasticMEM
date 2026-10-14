using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Runtime;
using ElasticMem.Core.Research;
namespace ElasticMem.App.Research;
internal static class LiveBackendResearch
{
    const int Pages=16384, Operations=8192, Writes=512, Seed=23019;
    static double Us(long t)=>(Stopwatch.GetTimestamp()-t)*1e6/Stopwatch.Frequency;
    static byte[] Page(int key,int generation,string workload)
    {
        var b=new byte[4096];var rng=new Random(unchecked(Seed+key*65537+generation*997));
        if(workload=="random"||key%4==0)rng.NextBytes(b);
        else{byte value=(byte)(key%251);Array.Fill(b,value);rng.NextBytes(b.AsSpan(0,128));BitConverter.TryWriteBytes(b.AsSpan(128,4),generation);}
        return b;
    }
    static void Collect(){GCSettings.LargeObjectHeapCompactionMode=GCLargeObjectHeapCompactionMode.CompactOnce;GC.Collect(GC.MaxGeneration,GCCollectionMode.Forced,true,true);GC.WaitForPendingFinalizers();GC.Collect(GC.MaxGeneration,GCCollectionMode.Forced,true,true);}
    static object Memory(Process self)
    {
        Collect();var ws=new List<double>();var commit=new List<double>();var free=new List<double>();
        for(int i=0;i<9;i++){if(!Native.TryGetMemInfo(self.Handle,out var m))throw new IOException("Own worker memory counters unavailable");ws.Add(m.WorkingSetSize.ToUInt64());commit.Add(m.PrivateUsage.ToUInt64());var a=Native.MemoryStatus().ullAvailPhys;if(a<512UL<<20)throw new NotSupportedException("Live worker free RAM pressure guard");free.Add(a);Thread.Sleep(50);}
        return new{samples=9,interval_ms=50,working_set_median_bytes=CodecBench.Quantile(ws,.5),private_commit_median_bytes=CodecBench.Quantile(commit,.5),system_available_median_bytes=CodecBench.Quantile(free,.5),measurement="whole own child WS includes shared image pages; private usage is commit, not resident RAM; system available is noisy context"};
    }
    internal static object Worker(string mode,string workload,string runId)
    {
        if(mode is not("raw" or "zstd1")||workload is not("structured" or "random")||!Guid.TryParseExact(runId,"N",out _))throw new ArgumentException("Invalid fixed live AB worker parameters");
        if(Native.MemoryStatus().ullAvailPhys<2UL<<30)throw new NotSupportedException("Live AB needs2GiB currently available RAM");
        using var self=Process.GetCurrentProcess();using var store=new LivePageStore(mode,Pages,256);
        // Warm both API and codec before baseline; identical native/managed runtime path.
        for(int i=0;i<32;i++){store.Put(i,Page(i,0,workload));_ =store.Get(i);store.Delete(i);}
        var baseline=Memory(self);var total=Stopwatch.StartNew();var versions=new int[Pages];
        for(int i=0;i<Pages;i++){if(i%1024==0&&Native.MemoryStatus().ullAvailPhys<1UL<<30)throw new NotSupportedException("Live fill pressure guard");store.Put(i,Page(i,0,workload));}
        var filled=Memory(self);
        for(int i=0;i<128;i++)_ =store.Get(i);
        var rng=new Random(Seed);var reads=new List<double>(Operations);var writes=new List<double>(Writes);ulong checksum=0;
        self.Refresh();double cpuBefore=self.TotalProcessorTime.TotalMilliseconds;
        if(!Native.TryGetMemInfo(self.Handle,out var before))throw new IOException("Own before fault counters unavailable");
        var operationsClock=Stopwatch.StartNew();
        for(int op=0;op<Operations;op++)
        {
            int key=op%5==0?rng.Next(Pages):rng.Next(128);long t=Stopwatch.GetTimestamp();byte[] got=store.Get(key);reads.Add(Us(t));
            if(!got.AsSpan().SequenceEqual(Page(key,versions[key],workload)))throw new InvalidDataException("Live read full byte mismatch");checksum=unchecked(checksum+BitConverter.ToUInt64(got,0));
            if(op%16==0){int changed=rng.Next(Pages);byte[] page=Page(changed,++versions[changed],workload);t=Stopwatch.GetTimestamp();store.Put(changed,page);writes.Add(Us(t));}
            if(op%1024==0&&Native.MemoryStatus().ullAvailPhys<512UL<<20)throw new NotSupportedException("Live operation pressure guard");
        }
        operationsClock.Stop();self.Refresh();double cpuAfter=self.TotalProcessorTime.TotalMilliseconds;
        if(!Native.TryGetMemInfo(self.Handle,out var after))throw new IOException("Own after fault counters unavailable");
        var afterWork=Memory(self);var statsAfterWork=store.Stats();
        // Never treat sampled success as integrity: validate every stored page after writes.
        for(int i=0;i<Pages;i++)if(!store.Get(i).AsSpan().SequenceEqual(Page(i,versions[i],workload)))throw new InvalidDataException("Final live store mismatch");
        return new{schema="elasticmem.live.ab.worker.v1",status="owned_live_store_roundtrip_checked",pid=self.Id,start_ticks=self.StartTime.ToUniversalTime().Ticks,run_id=runId,mode,workload,seed=Seed,pages=Pages,input_bytes=(long)Pages*4096,operations=Operations,writes=Writes,baseline,filled,after_work=afterWork,store_after_work=statsAfterWork,
            read_p50_us=CodecBench.Quantile(reads,.5),read_p95_us=CodecBench.Quantile(reads,.95),read_p99_us=CodecBench.Quantile(reads,.99),write_p50_us=CodecBench.Quantile(writes,.5),write_p95_us=CodecBench.Quantile(writes,.95),write_p99_us=CodecBench.Quantile(writes,.99),fixture_wall_seconds=operationsClock.Elapsed.TotalSeconds,fixture_cpu_ms=cpuAfter-cpuBefore,page_fault_delta=unchecked(after.PageFaultCount-before.PageFaultCount),checksum=checksum.ToString("X16"),all_pages_after_writes_verified=true,elapsed_seconds=total.Elapsed.TotalSeconds,
            limitations=new[]{"Actual owned backend read/write/cache/hash/copy timing,not isolated codec decode; validation and data generation excluded from per-operation latency but INCLUDED in wall/CPU","Memory checkpoints follow forced full GC in BOTH modes; real agent GC and IPC are not modeled","Fixed synthetic64MiB pages: structured has75% repetitive+128B random headers,25% seeded random; random is incompressible negative control","80% reads in128hot pages,20% uniformly anywhere,1write per16reads; fixed seed,not inferred browser access","Read p99 is backend call latency,not pagefile fault/browser or Telegram latency; PageFaultCount mixes soft/hard","Whole-process WS is observed resident working set,not exclusive physical pages or a desktop RAM saving; commit/system available are separate"}};
    }
    internal static object Run(ResearchRun run,IProgress<string>? progress,CancellationToken ct)
    {
        AutoResearch.EnsureNoOtherInstances();var rows=new List<JsonElement>();var pairs=new List<object>();
        foreach(string workload in new[]{"structured","random"})for(int pair=0;pair<3;pair++)
        {
            var measured=new Dictionary<string,JsonElement>();
            foreach(string mode in pair%2==0?new[]{"raw","zstd1"}:new[]{"zstd1","raw"})
            {
                ct.ThrowIfCancellationRequested();if(Native.MemoryStatus().ullAvailPhys<2UL<<30)throw new NotSupportedException("Live AB needs2GiB available; never induce system pressure");
                string id=Guid.NewGuid().ToString("N"),path=run.FilePath($"inputs/live-{workload}-{pair}-{mode}.json");
                var psi=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};
                foreach(string arg in new[]{"--research","live-ab-worker","--mode",mode,"--workload",workload,"--run-id",id,"--out",path})psi.ArgumentList.Add(arg);
                progress?.Report($"Live А/Б: {workload}, пара {pair+1}/3, {mode}; только собственный child");
                using var child=Process.Start(psi)??throw new IOException("Own live child failed to start");Exception? primary=null;
                try
                {
                    long start=child.StartTime.ToUniversalTime().Ticks;var timer=Stopwatch.StartNew();
                    while(!child.WaitForExit(100)){ct.ThrowIfCancellationRequested();if(timer.Elapsed.TotalSeconds>180)throw new TimeoutException("Live child timeout");}
                    ct.ThrowIfCancellationRequested();if(child.ExitCode!=0)throw new IOException("Live child failed; local inputs report preserved");
                    if(new FileInfo(path).Length>1048576)throw new InvalidDataException("Live child report cap");
                    using var doc=JsonDocument.Parse(File.ReadAllText(path));var d=doc.RootElement;
                    if(d.GetProperty("schema").GetString()!="elasticmem.live.ab.worker.v1"||d.GetProperty("status").GetString()!="owned_live_store_roundtrip_checked"||d.GetProperty("pid").GetInt32()!=child.Id||d.GetProperty("start_ticks").GetInt64()!=start||d.GetProperty("run_id").GetString()!=id||d.GetProperty("mode").GetString()!=mode||d.GetProperty("workload").GetString()!=workload||d.GetProperty("seed").GetInt32()!=Seed||d.GetProperty("pages").GetInt32()!=Pages||d.GetProperty("operations").GetInt32()!=Operations||d.GetProperty("writes").GetInt32()!=Writes||!d.GetProperty("all_pages_after_writes_verified").GetBoolean())throw new InvalidDataException("Live child identity/plan/result mismatch");
                    measured.Add(mode,d.Clone());rows.Add(d.Clone());
                }
                catch(Exception ex){primary=ex;throw;}
                finally{try{if(!child.HasExited){child.Kill();if(!child.WaitForExit(10000))throw new IOException("Own live child cleanup timeout");}}catch(Exception ex){run.Metadata["owned_child_cleanup_error"]=ex.ToString();if(primary==null)throw;primary.Data["owned_child_cleanup_error"]=ex.ToString();}}
            }
            var a=measured["raw"];var b=measured["zstd1"];if(a.GetProperty("checksum").GetString()!=b.GetProperty("checksum").GetString())throw new InvalidDataException("A/B workload checksum differs");
            double Increment(JsonElement d,string key)=>d.GetProperty("after_work").GetProperty(key).GetDouble()-d.GetProperty("baseline").GetProperty(key).GetDouble();
            pairs.Add(new{workload,pair=pair+1,order=pair%2==0?"AB":"BA",
                a_working_set_bytes=a.GetProperty("after_work").GetProperty("working_set_median_bytes").GetDouble(),b_working_set_bytes=b.GetProperty("after_work").GetProperty("working_set_median_bytes").GetDouble(),
                observed_absolute_working_set_reduction_bytes=a.GetProperty("after_work").GetProperty("working_set_median_bytes").GetDouble()-b.GetProperty("after_work").GetProperty("working_set_median_bytes").GetDouble(),observed_incremental_working_set_reduction_bytes=Increment(a,"working_set_median_bytes")-Increment(b,"working_set_median_bytes"),observed_incremental_private_commit_reduction_bytes=Increment(a,"private_commit_median_bytes")-Increment(b,"private_commit_median_bytes"),a_read_p50_us=a.GetProperty("read_p50_us").GetDouble(),b_read_p50_us=b.GetProperty("read_p50_us").GetDouble(),a_read_p99_us=a.GetProperty("read_p99_us").GetDouble(),b_read_p99_us=b.GetProperty("read_p99_us").GetDouble(),a_write_p99_us=a.GetProperty("write_p99_us").GetDouble(),b_write_p99_us=b.GetProperty("write_p99_us").GetDouble(),checksum_equal=true});
        }
        var ipc=ProbeIpc(run,ct);
        return new{schema="elasticmem.live.ab.v1",ipc_probe=ipc,status="owned_cooperative_live_ab_not_browser_or_telegram",pairs,workers=rows,transparent_foreign_backend_supported=false,chrome_telegram_ram_saved_bytes=(long?)null,
            limitations=new[]{"Primary comparison preset: A/raw B/zstd1,2fixed workloads,3pairs/orderAB BA AB; descriptive repeated runs,not independent realapp evidence or confidence intervals","Positive incremental WS reduction concerns ONLY own cooperative child at measured checkpoints; it is not total unique physical/system RAM","No generic benefit gate: memory and latency tradeoff must be accepted by the client; random negative control may show overhead","Backend usable only when client owns and releases original pages; browser/Telegram integration absent","No foreign capture/trim/injection/kernel/pagefile/settings changes; no automatic user data upload"}};
    }
    internal static string Describe(object result)
    {
        var d=JsonSerializer.SerializeToElement(result,ResearchRun.Json);
        var text=new StringBuilder("Live А/Б: только собственный cooperative backend\r\nНЕ прозрачное сжатие Chrome/Telegram. WS — рабочий набор процесса, НЕ уникальная физическая RAM всей системы.\r\nA/raw, B/zstd1;64МиБ;2синтетические нагрузки,3парыAB/BA;9 memory samples after fullGC.\r\n\r\n");
        foreach(var p in d.GetProperty("pairs").EnumerateArray())text.AppendLine(FormattableString.Invariant($"{p.GetProperty("workload").GetString()} pair{p.GetProperty("pair").GetInt32()} {p.GetProperty("order").GetString()}: WS A={p.GetProperty("a_working_set_bytes").GetDouble()/1048576:F2}MiB B={p.GetProperty("b_working_set_bytes").GetDouble()/1048576:F2}MiB; observed A-B={p.GetProperty("observed_absolute_working_set_reduction_bytes").GetDouble()/1048576:F2}MiB; read p99 A={p.GetProperty("a_read_p99_us").GetDouble():F2}us B={p.GetProperty("b_read_p99_us").GetDouble():F2}us; write p99 A={p.GetProperty("a_write_p99_us").GetDouble():F2}us B={p.GetProperty("b_write_p99_us").GetDouble():F2}us; checksum equal."));
        text.AppendLine("\r\nGet/Put timings: полный backend API,без IPC/fixture validation; wall/CPU fixture включают генерацию и проверку. IPC ниже — warmed transport smoke,НЕcold restore.");
        foreach(var p in d.GetProperty("ipc_probe").EnumerateArray())text.AppendLine(FormattableString.Invariant($"IPC {p.GetProperty("mode").GetString()}:p50={p.GetProperty("read_p50_us").GetDouble():F2}us,p99={p.GetProperty("read_p99_us").GetDouble():F2}us;64 measuredGet/allfitcache;byte checks passed."));
        text.AppendLine("\r\nПольза для реального агента требует измерения полного клиента+backend. Нельзя переносить результат64МиБ fixtures на браузер/Telegram. Данныеbackendvolatile:неединственнаякопияважныхданных. Negative/random может не дать RAMвыигрыша. Полные counters/limitations — result.json.");return text.ToString();
    }
    static object ProbeIpc(ResearchRun run,CancellationToken ct)
    {
        var results=new List<object>();
        foreach(string mode in new[]{"raw","zstd1"})
        {
            var psi=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true};
            foreach(string arg in new[]{"--live-backend","--backend-mode",mode})psi.ArgumentList.Add(arg);
            ct.ThrowIfCancellationRequested();using var child=Process.Start(psi)??throw new IOException("Own IPC child start failed");Exception? primary=null;
            try
            {
                long identity=child.StartTime.ToUniversalTime().Ticks;
                JsonElement Call(object request)
                {
                    ct.ThrowIfCancellationRequested();if(child.HasExited)throw new IOException("IPC child exited");
                    using var rpc=CancellationTokenSource.CreateLinkedTokenSource(ct);rpc.CancelAfter(TimeSpan.FromSeconds(10));
                    string? reply;
                    try
                    {
                        child.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(),rpc.Token).WaitAsync(rpc.Token).GetAwaiter().GetResult();
                        child.StandardInput.FlushAsync(rpc.Token).WaitAsync(rpc.Token).GetAwaiter().GetResult();
                        reply=child.StandardOutput.ReadLineAsync(rpc.Token).AsTask().WaitAsync(rpc.Token).GetAwaiter().GetResult();
                    }
                    catch(OperationCanceledException ex)when(!ct.IsCancellationRequested){throw new TimeoutException("Whole IPC RPC exceeded10seconds",ex);}
                    if(reply==null||reply.Length>12000)throw new InvalidDataException("IPC reply missing/oversize");
                    using var d=JsonDocument.Parse(reply);if(!d.RootElement.GetProperty("ok").GetBoolean())throw new InvalidDataException("IPC command failed");return d.RootElement.Clone();
                }
                for(int i=0;i<64;i++)Call(new{cmd="put",key=i,page_base64=Convert.ToBase64String(Page(i,0,"structured"))});
                var reads=new List<double>();for(int i=0;i<128;i++){int k=i%64;long t=Stopwatch.GetTimestamp();var response=Call(new{cmd="get",key=k});byte[] b=Convert.FromBase64String(response.GetProperty("page_base64").GetString()!);double us=Us(t);if(!b.AsSpan().SequenceEqual(Page(k,0,"structured")))throw new InvalidDataException("IPC full byte mismatch");if(i>=64)reads.Add(us);}
                var stats=Call(new{cmd="stats"}).GetProperty("stats").Clone();Call(new{cmd="quit"});child.StandardInput.Close();if(!child.WaitForExit(10000)||child.ExitCode!=0)throw new IOException("IPC shutdown failed");
                results.Add(new{mode,pid=child.Id,start_ticks=identity,pages=64,warmup_gets=64,measured_gets=64,read_p50_us=CodecBench.Quantile(reads,.5),read_p95_us=CodecBench.Quantile(reads,.95),read_p99_us=CodecBench.Quantile(reads,.99),store=stats,full_bytes_verified=true,limitations=new[]{"Own child stdio JSON/base64 roundtrip;64pages all fit hot cache after warmup,NOT cold decode or Python/Node throughput","Timing includes localIPC,serialization,responseparse,base64;bytecomparison excluded;sequential raw thenzstd,exploratory transport smoke test"}});
            }
            catch(Exception ex){primary=ex;throw;}
            finally{try{if(!child.HasExited){child.Kill();if(!child.WaitForExit(10000))throw new IOException("Own IPC child cleanup timeout");}}catch(Exception ex){run.Metadata["owned_ipc_cleanup_error"]=ex.ToString();if(primary==null)throw;primary.Data["owned_ipc_cleanup_error"]=ex.ToString();}}
        }
        return results;
    }
    internal static void Protocol(string mode)
    {
        using var input=new StreamReader(Console.OpenStandardInput(),Encoding.UTF8);
        using var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)){AutoFlush=true};
        Environment.ExitCode=LiveStoreProtocol.Run(input,output,mode);
    }
}
