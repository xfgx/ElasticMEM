using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ElasticMem.Core.Research;
namespace ElasticMem.App.Research;
internal static unsafe class ControlledResearch
{
    [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr VirtualAlloc(IntPtr a,nuint size,uint type,uint protection);
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool VirtualFree(IntPtr a,nuint size,uint type);
    [DllImport("psapi.dll",SetLastError=true)]static extern bool EmptyWorkingSet(IntPtr h);
    [StructLayout(LayoutKind.Sequential)]struct WS {public IntPtr Address;public UIntPtr Flags;}
    [DllImport("kernel32.dll",EntryPoint="K32QueryWorkingSetEx",SetLastError=true)]static extern bool QueryWorkingSet(IntPtr h,[In,Out]WS[] pages,uint bytes);
    static bool[] Valid(IntPtr handle,WS[] pages)
    {
        if(!QueryWorkingSet(handle,pages,checked((uint)(pages.Length*Marshal.SizeOf<WS>()))))
        {int error=Marshal.GetLastWin32Error();throw new System.ComponentModel.Win32Exception(error,"Own controlled arena QueryWorkingSetEx failed");}
        return pages.Select(x=>(x.Flags.ToUInt64()&1)!=0).ToArray();
    }
    static object LifecycleWorker(Process self,Stopwatch total)
    {
        var plan=LifecycleWorkload.Plan();var payload=ControlledWorkload.Contents();var before=new bool[256][];var after=new bool[256][];
        var states=new WS[128];var generationBytes=new byte[128*256];long commits=0,decommits=0;ulong checksum=0,expected=0;
        IntPtr arena=VirtualAlloc(IntPtr.Zero,128*4096u,0x2000,4);
        if(arena==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Lifecycle reserve failed");
        Exception? lifecycleError=null;
        try
        {
            for(int p=0;p<128;p++)states[p].Address=arena+p*4096;
            for(int t=0;t<256;t++)
            {
                if(total.Elapsed.TotalSeconds>150)throw new TimeoutException("Controlled lifecycle time guard");
                if(Native.MemoryStatus().ullAvailPhys<512UL<<20)throw new NotSupportedException("Lifecycle free RAM guard");
                for(int p=0;p<128;p++)
                {
                    bool live=plan.Alive[t][p],was=t>0&&plan.Alive[t-1][p];generationBytes[t*128+p]=plan.Generation[t][p];
                    if(!live&&was){if(!VirtualFree(states[p].Address,4096,0x4000))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Lifecycle decommit failed");decommits++;}
                    if(live&&!was)
                    {
                        if(VirtualAlloc(states[p].Address,4096,0x1000,4)!=states[p].Address)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Lifecycle commit failed");
                        Marshal.Copy(payload[p],0,states[p].Address,4096);((ulong*)arena)[p*512]=LifecycleWorkload.Word(payload[p],plan.Generation[t][p]);commits++;
                    }
                }
                if(!EmptyWorkingSet(self.Handle))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Lifecycle own trim failed");
                before[t]=Valid(self.Handle,states);
                for(int p=0;p<128;p++)if(plan.Touch[t][p])
                {checksum=unchecked(checksum+Volatile.Read(ref ((ulong*)arena)[p*512]));expected=unchecked(expected+LifecycleWorkload.Word(payload[p],plan.Generation[t][p]));}
                after[t]=Valid(self.Handle,states);Thread.Sleep(20);
            }
            if(checksum!=expected)throw new InvalidDataException("Lifecycle native generation/read checksum mismatch");
        }
        catch(Exception ex){lifecycleError=ex;throw;}
        finally
        {
            if(!VirtualFree(arena,0,0x8000))
            {var cleanup=new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Lifecycle reservation release failed");if(lifecycleError!=null)throw new AggregateException("Lifecycle primary and release errors",lifecycleError,cleanup);throw cleanup;}
        }
        return new{name=LifecycleWorkload.Name,pages=128,epochs=256,alive=ControlledWorkload.Pack(plan.Alive),touch=ControlledWorkload.Pack(plan.Touch),
            before_valid=ControlledWorkload.Pack(before),after_valid=ControlledWorkload.Pack(after),generations=Convert.ToBase64String(generationBytes),checksum,expected_checksum=expected,committed_pages=commits,decommitted_pages=decommits};
    }
    public static object Worker()
    {
        if(!OperatingSystem.IsWindows()||!Environment.Is64BitProcess)throw new NotSupportedException("Windows x64 controlled worker required");
        using var self=Process.GetCurrentProcess();var total=Stopwatch.StartNew();var results=new List<object>();
        if(!Native.IsWow64Process2(self.Handle,out var pm,out var nm)||pm!=0||nm!=0x8664)throw new NotSupportedException("Controlled native x64 worker required");
        foreach(string name in ControlledWorkload.Cases)
        {
            var truth=ControlledWorkload.Activity(name);var contents=ControlledWorkload.Contents();
            var observed=new bool[ControlledWorkload.Epochs][];var known=new bool[ControlledWorkload.Epochs][];
            var states=new WS[ControlledWorkload.Pages];var timing=new List<object>();
            IntPtr arena=VirtualAlloc(IntPtr.Zero,ControlledWorkload.Pages*4096u,0x3000,4);
            if(arena==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Own arena allocation");
            ulong checksum=0,expected=0;bool released;
            try
            {
                for(int p=0;p<ControlledWorkload.Pages;p++){states[p].Address=arena+p*4096;Marshal.Copy(contents[p],0,states[p].Address,4096);}
                for(int t=0;t<ControlledWorkload.Epochs;t++)
                {
                    if(total.Elapsed.TotalSeconds>150)throw new TimeoutException("Controlled worker time guard");
                    if(Native.MemoryStatus().ullAvailPhys<512UL<<20)throw new NotSupportedException("Controlled worker free RAM guard");
                    long started=Stopwatch.GetTimestamp();
                    if(!EmptyWorkingSet(self.Handle)){int error=Marshal.GetLastWin32Error();throw new System.ComponentModel.Win32Exception(error,"Own controlled trim failed");}
                    var before=Valid(self.Handle,states);long touchStart=Stopwatch.GetTimestamp();
                    // Only designated reads touch the native arena after initialization.
                    // GC scans neither this unmanaged arena nor its contents.
                    for(int p=0;p<ControlledWorkload.Pages;p++)if(truth[t][p])
                    {checksum=unchecked(checksum+Volatile.Read(ref ((ulong*)arena)[p*512]));expected=unchecked(expected+BitConverter.ToUInt64(contents[p],0));}
                    double touchUs=(Stopwatch.GetTimestamp()-touchStart)*1e6/Stopwatch.Frequency;
                    var end=Valid(self.Handle,states);
                    known[t]=before.Select(x=>!x).ToArray();
                    observed[t]=end.Select((x,p)=>known[t][p]&&x).ToArray();
                    timing.Add(new{epoch=t,retained=before.Count(x=>x),scheduled=truth[t].Count(x=>x),touch_us=touchUs,probe_and_touch_us=(Stopwatch.GetTimestamp()-started)*1e6/Stopwatch.Frequency});
                    Thread.Sleep(20); // Synthetic logical epochs,NOT one-second real workloads.
                }
                if(checksum!=expected)throw new InvalidDataException("Controlled native checksum mismatch");
            }
            finally{released=VirtualFree(arena,0,0x8000);}
            if(!released)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Own arena free failed");
            results.Add(new{name,pages=ControlledWorkload.Pages,epochs=ControlledWorkload.Epochs,checksum,expected_checksum=expected,
                truth=ControlledWorkload.Pack(truth),observed=ControlledWorkload.Pack(observed),known=ControlledWorkload.Pack(known),timing});
        }
        var lifecycle=LifecycleWorker(self,total);
        return new{schema="elasticmem.controlled.worker.v2",status="controlled_arena_completed",pid=self.Id,start_ticks=self.StartTime.ToUniversalTime().Ticks,
            seed=ControlledWorkload.Seed,elapsed_seconds=total.Elapsed.TotalSeconds,cases=results,lifecycle};
    }
    public static object Run(ResearchRun run,IProgress<string>? progress,CancellationToken ct)
    {
        if(Native.MemoryStatus().ullAvailPhys<512UL<<20)throw new NotSupportedException("Own child free RAM guard");
        string target=run.FilePath("inputs/controlled-child.json");
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};
        start.ArgumentList.Add("--research");start.ArgumentList.Add("controlled-worker");start.ArgumentList.Add("--out");start.ArgumentList.Add(target);
        ct.ThrowIfCancellationRequested();using var child=Process.Start(start)??throw new IOException("Cannot start own controlled worker");
        Exception? primaryError=null;
        try
        {
            long expectedStart=child.StartTime.ToUniversalTime().Ticks;var timer=Stopwatch.StartNew();
            progress?.Report("Контролируемый стенд: только собственный child, три синтетические нагрузки");
            while(!child.WaitForExit(100)){ct.ThrowIfCancellationRequested();if(timer.Elapsed.TotalSeconds>180)throw new TimeoutException("Controlled child exceeded180seconds");}
            ct.ThrowIfCancellationRequested();
            if(child.ExitCode!=0)throw new IOException("Controlled child failed; private child report preserved");
            if(new FileInfo(target).Length>4L<<20)throw new InvalidDataException("Controlled child report cap");
            using var doc=JsonDocument.Parse(File.ReadAllText(target));var root=doc.RootElement;
            if(root.GetProperty("schema").GetString()!="elasticmem.controlled.worker.v2"||root.GetProperty("status").GetString()!="controlled_arena_completed"||
               root.GetProperty("pid").GetInt32()!=child.Id||root.GetProperty("start_ticks").GetInt64()!=expectedStart||root.GetProperty("seed").GetInt32()!=ControlledWorkload.Seed)throw new InvalidDataException("Own controlled child identity/schema");
            var rows=root.GetProperty("cases").EnumerateArray().ToArray();
            if(rows.Length!=3||!rows.Select(x=>x.GetProperty("name").GetString()).SequenceEqual(ControlledWorkload.Cases))throw new InvalidDataException("Controlled case roster/order");
            var results=new List<object>();var payload=ControlledWorkload.Contents();var addresses=Enumerable.Range(1,ControlledWorkload.Pages).Select(i=>(ulong)i*4096).ToArray();
            foreach(var row in rows)
            {
                ct.ThrowIfCancellationRequested();string name=row.GetProperty("name").GetString()!;
                var decoded=ControlledWorkload.DecodeCase(row,name);var truth=decoded.Truth;var observation=decoded.Observed;var known=decoded.Known;
                var allKnown=truth.Select(x=>Enumerable.Repeat(true,x.Length).ToArray()).ToArray();
                var source=new CodecInput(child.Id,expectedStart,addresses,payload);
                progress?.Report("Контролируемый Г5: "+name+" — ground truth / Windows proxy");
                var exact=CausalGrouping.Run(source,truth,allKnown,ct);var proxy=CausalGrouping.Run(source,observation,known,ct);
                results.Add(new{name,pages=ControlledWorkload.Pages,epochs=ControlledWorkload.Epochs,
                    observation=ControlledWorkload.Compare(truth,observation,known),ground_truth_causal=exact,windows_proxy_causal=proxy,
                    synthetic_trace=new{layout="row-major epoch*128+page;LSB-first;logical indices,NOT native addresses",truth=row.GetProperty("truth").GetString(),observed=row.GetProperty("observed").GetString(),known=row.GetProperty("known").GetString()},
                    timing=row.GetProperty("timing").Clone(),checksum_verified=true});
            }
            var lifecycle=LifecycleWorkload.Analyze(root.GetProperty("lifecycle"));
            return new{schema="elasticmem.controlled.research.v2",status="controlled_synthetic_validation_not_browser_evidence",
                child_pid=child.Id,child_start_ticks=expectedStart,seed=ControlledWorkload.Seed,cases=results,lifecycle,
                primary_comparison=new{threshold_epochs=2,block_pages=new[]{4,16},definition="Predeclared primary synthetic comparison; all other thresholds exploratory; no tuning on TEST"},
                foreign_process_trim=false,system_settings_changed=false,
                limitations=new[]{"Only self-created child; three fixed128page controls plus separate decommit/recommit lifecycle control;256logicalepochs each,20ms sleeps",
                "Ground truth is explicit volatile reads of the designated arena ONLY,not all child/desktop accesses or actual fault backing",
                "Lifecycle is a separate fixed-reservation generation test,NOT causal G5; three constructed correlated positive/adjacency controls and seeded independent fixed-density control,NOT evidence of benefit in real renderers",
                "No physical RAM savings,GC attribution,pagefile/NVMe latency or live pager; synthetic payload sizes/CPU do not generalize",
                "Proxy known mask excludes retained-after-trim pages; unknown strict G5 handling unchanged; conditional recall is not whole-population recall",
                "Truth and proxy have different missingness; their grouping results are separate diagnostic replays,NOT a paired speedup estimate",
                "Same-trigger-epoch utility; primary T2/cap4,16 fixed before observations; other63-row scans exploratory",
                "Synthetic bitmaps are exported as logical page indices only; no native addresses or user memory content"}};
        }
        catch(Exception ex){primaryError=ex;throw;}
        finally
        {
            try{if(!child.HasExited){child.Kill();if(!child.WaitForExit(10000))throw new IOException("Own controlled child did not terminate");}}
            catch(Exception cleanup){run.Metadata["owned_child_cleanup_error"]=cleanup.ToString();if(primaryError==null)throw;primaryError.Data["owned_child_cleanup_error"]=cleanup.ToString();}
        }
    }
}