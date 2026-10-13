using System.Diagnostics;
using System.Runtime.InteropServices;
using ElasticMem.Core.Research;
namespace ElasticMem.App.Research;
internal static class EpochResearch
{
    [StructLayout(LayoutKind.Sequential)]struct WS {public IntPtr Address;public UIntPtr Flags;}
    [DllImport("kernel32.dll",EntryPoint="K32QueryWorkingSetEx",SetLastError=true)]static extern bool QueryWorkingSet(IntPtr h,[In,Out]WS[] pages,uint length);
    [DllImport("psapi.dll",SetLastError=true)]static extern bool EmptyWorkingSet(IntPtr h);
    [StructLayout(LayoutKind.Sequential)]struct SystemInfo{public uint Architecture,PageSize;public IntPtr Minimum,Maximum;public UIntPtr Mask;public uint Count,ProcessorType,Granularity;public ushort Level,Revision;}
    [DllImport("kernel32.dll")]static extern void GetNativeSystemInfo(out SystemInfo info);
    static bool[] Valid(IntPtr h,ulong[] addresses)
    {
        var result=new bool[addresses.Length];
        for(int at=0;at<addresses.Length;at+=4096)
        {
            var p=addresses.Skip(at).Take(4096).Select(a=>new WS{Address=(IntPtr)(long)a}).ToArray();
            if(!QueryWorkingSet(h,p,checked((uint)(p.Length*Marshal.SizeOf<WS>()))))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            for(int i=0;i<p.Length;i++)result[at+i]=(p[i].Flags.ToUInt64()&1)!=0;
        }
        return result;
    }
    static ulong[] Allocations(IntPtr h,ulong[] addresses)
    {
        var result=new ulong[addresses.Length];Native.MEMORY_BASIC_INFORMATION m=default;ulong lo=0,size=0;
        for(int i=0;i<addresses.Length;i++)
        {
            ulong a=addresses[i];
            if(!PageMapping.Contains(lo,size,a,4096))
            {
                if(Native.VirtualQueryEx(h,(IntPtr)(long)a,out m,(IntPtr)Marshal.SizeOf<Native.MEMORY_BASIC_INFORMATION>())==IntPtr.Zero){size=0;continue;}
                lo=(ulong)m.BaseAddress.ToInt64();size=m.RegionSize.ToUInt64();
            }
            if(m.State==Native.MEM_COMMIT&&m.Type==PageEligibility.Private&&(m.Protect&(Native.PAGE_GUARD|Native.PAGE_NOACCESS))==0&&(m.Protect&Native.ReadableMask)!=0&&PageMapping.Contains(lo,size,a,4096))
                result[i]=(ulong)m.AllocationBase.ToInt64();
        }
        return result;
    }
    static (ulong[] Pages,long Eligible,bool Complete) Census(IntPtr h,CancellationToken ct)
    {
        const int cap=262144;var pages=new List<ulong>();long eligible=0;ulong cursor=0;bool complete=true;GetNativeSystemInfo(out var system);ulong limit=checked((ulong)system.Maximum.ToInt64()+1);
        while(cursor<limit)
        {
            ct.ThrowIfCancellationRequested();
            if(Native.VirtualQueryEx(h,(IntPtr)(long)cursor,out var m,(IntPtr)Marshal.SizeOf<Native.MEMORY_BASIC_INFORMATION>())==IntPtr.Zero){complete=false;break;}
            ulong lo=(ulong)m.BaseAddress.ToInt64(),size=m.RegionSize.ToUInt64();
            if(size==0||lo>ulong.MaxValue-size||lo+size<=cursor){complete=false;break;}
            if(m.State==Native.MEM_COMMIT&&m.Type==PageEligibility.Private&&(m.Protect&(Native.PAGE_GUARD|Native.PAGE_NOACCESS))==0&&(m.Protect&Native.ReadableMask)!=0)
            {
                eligible+=(long)(size/4096);
                for(ulong off=0;off+4096<=size&&pages.Count<cap;off+=4096)pages.Add(lo+off);
            }
            cursor=lo+size;
        }
        // VirtualQueryEx commonly stops at the end of the user address range;
        // retain incomplete explicitly rather than assuming a complete census.
        return(pages.ToArray(),eligible,complete);
    }
    public static object Run(CodecInput[] input,bool allowTrim,int epochs,ResearchRun run,IProgress<string>? progress,CancellationToken ct,bool extended=false)
    {
        if(!allowTrim)throw new ArgumentException("Explicit --allow-working-set-trim required; never disable pagefile/compression");if(epochs is <8 or >900)throw new ArgumentException("Epoch count8..900");var result=new List<object>();var counters=new List<string>{"pid,phase,epoch,elapsed_ms,working_set_bytes,page_fault_count,retained_after_trim,valid_at_end,observed_reacquired,trim_call_us"};
        foreach(var source in input.Take(extended?4:2))
        {
            ct.ThrowIfCancellationRequested();if(source.Pid<=4||source.Pid==Environment.ProcessId)throw new ArgumentException("Foreign ordinary application only");
            using var p=Process.GetProcessById(source.Pid);if(!new[]{"chrome","msedge","steamwebhelper"}.Contains(p.ProcessName,StringComparer.OrdinalIgnoreCase)||p.SessionId!=Process.GetCurrentProcess().SessionId||p.StartTime.ToUniversalTime().Ticks!=source.StartTicks)throw new InvalidOperationException("Target identity/session changed");
            IntPtr h=Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION|Native.PROCESS_VM_READ|0x100,false,source.Pid);if(h==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if(!Native.GetProcessTimes(h,out var creation,out var exit,out _,out _)||exit!=0||DateTime.FromFileTimeUtc(creation).Ticks!=source.StartTicks)throw new InvalidOperationException("Handle identity changed");
                AutoResearch.ConfirmRenderer(h,source.Pid,p.ProcessName,source.StartTicks);
                // Spread over captured pages only; do not claim a stratified full-process sample.
                var census=extended?Census(h,ct):(source.Addresses, (long)source.Addresses.Length,false);
                var addresses=extended?census.Item1:Enumerable.Range(0,Math.Min(source.Addresses.Length,1024)).Select(i=>source.Addresses[i*source.Addresses.Length/Math.Min(source.Addresses.Length,1024)]).ToArray();
                if(addresses.Length==0)throw new IOException("No committed readable private pages");
                if((ulong)((long)addresses.Length*epochs*4+(512L<<20))>Native.MemoryStatus().ullAvailPhys/2)throw new NotSupportedException("Epoch history would exceed half available RAM; no pressure induced");
                var original=Allocations(h,addresses);var allKnown=original.Select(x=>x!=0).ToArray();var activity=new List<bool[]>();var knownRows=new List<bool[]>();var durations=new List<double>();var phases=new List<object>();
                foreach(string phase in new[]{"baseline_counters","trim_counters","trim_and_residency"})
                {
                    if(!Native.TryGetMemInfo(h,out var before))throw new IOException("Missing pre-phase counters");if(!Native.GetProcessTimes(h,out _,out _,out var kernelBefore,out var userBefore))throw new IOException("Missing pre-phase CPU times");var sw=Stopwatch.StartNew();int actual=phase=="trim_and_residency"?epochs:4;long faultsBefore=before.PageFaultCount;double trimTotal=0;
                    for(int i=0;i<actual;i++)
                    {
                        if(extended&&Native.MemoryStatus().ullAvailPhys<512UL<<20)throw new NotSupportedException("Low free memory guard; stop rather than induce paging pressure");
                        ct.ThrowIfCancellationRequested();progress?.Report($"Г0 эпохи: PID {source.Pid} {phase} {i+1}/{actual}");double trimUs=0;bool[]? after=null;var timer=Stopwatch.StartNew();if(phase!="baseline_counters"){AutoResearch.ConfirmRenderer(h,source.Pid,p.ProcessName,source.StartTicks);long t=Stopwatch.GetTimestamp();if(!EmptyWorkingSet(h))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"EmptyWorkingSet failed");trimUs=(Stopwatch.GetTimestamp()-t)*1e6/Stopwatch.Frequency;trimTotal+=trimUs;}
                        if(phase=="trim_and_residency")after=Valid(h,addresses);
                        int wait=Math.Max(0,1000-(int)timer.ElapsedMilliseconds);if(ct.WaitHandle.WaitOne(wait))ct.ThrowIfCancellationRequested();
                        int retained=0,endCount=0,reacquired=0;if(after!=null){var end=Valid(h,addresses);var allocation=Allocations(h,addresses);var observed=new bool[addresses.Length];var epochKnown=new bool[addresses.Length];for(int j=0;j<observed.Length;j++){bool known=original[j]!=0&&allocation[j]==original[j]&&!after[j];epochKnown[j]=known;allKnown[j]&=known;observed[j]=known&&end[j];if(after[j])retained++;if(end[j])endCount++;if(observed[j])reacquired++;}activity.Add(observed);knownRows.Add(epochKnown);durations.Add(timer.Elapsed.TotalSeconds);}
                        if(!Native.TryGetMemInfo(h,out var c))throw new IOException("Missing epoch counters");counters.Add(FormattableString.Invariant($"{source.Pid},{phase},{i},{sw.Elapsed.TotalMilliseconds:F3},{c.WorkingSetSize.ToUInt64()},{c.PageFaultCount},{retained},{endCount},{reacquired},{trimUs:F3}"));
                        if(!Native.GetProcessTimes(h,out var now,out var stopped,out _,out _)||now!=creation||stopped!=0)throw new InvalidOperationException("Process exited during epochs");
                    }
                    if(!Native.TryGetMemInfo(h,out var last))throw new IOException("Missing post-phase counters");if(!Native.GetProcessTimes(h,out _,out _,out var kernelAfter,out var userAfter))throw new IOException("Missing post-phase CPU times");phases.Add(new{phase,epochs=actual,elapsed_ms=sw.Elapsed.TotalMilliseconds,fault_delta=(long)last.PageFaultCount-faultsBefore,cpu_ms=((kernelAfter-kernelBefore)+(userAfter-userBefore))/10000d,working_set_before=before.WorkingSetSize.ToUInt64(),working_set_after=last.WorkingSetSize.ToUInt64(),trim_call_total_us=trimTotal,workload_throughput_measured=false});
                }
                ct.ThrowIfCancellationRequested();int[] included=Enumerable.Range(0,allKnown.Length).Where(i=>allKnown[i]).ToArray();var trace=activity.Select(a=>included.Select(i=>a[i]).ToArray()).ToArray();
                EpochTraceFile.Write(run.FilePath($"inputs/epochs-{source.Pid}.eet"),source.Pid,source.StartTicks,addresses,allKnown,activity.ToArray(),knownRows.ToArray());
                run.WriteJson($"inputs/epochs-{source.Pid}.json",new{pid=source.Pid,source.StartTicks,pages=addresses.Length,epochs=activity.Count,measured_epoch_seconds=durations,format="elasticmem.eet.v2;bitpacked;private"});
                var indices=addresses.Select((a,i)=>(a,i)).ToDictionary(x=>x.a,x=>x.i);
                int[] matched=Enumerable.Range(0,source.Addresses.Length).Where(i=>indices.TryGetValue(source.Addresses[i],out int j)&&knownRows.Take(activity.Count/2).All(a=>a[j])).Take(4096).ToArray();
                object grouping=matched.Length>=4?GroupingBench.Run(new CodecInput(source.Pid,source.StartTicks,matched.Select(i=>source.Addresses[i]).ToArray(),matched.Select(i=>source.Pages[i]).ToArray()),activity.Select(a=>matched.Select(i=>a[indices[source.Addresses[i]]]).ToArray()).ToArray(),run.FilePath("inputs/g5-"+source.Pid),ct,knownRows.Select(a=>matched.Select(i=>a[indices[source.Addresses[i]]]).ToArray()).ToArray()):new{status="skipped_no_complete_snapshot_intersection"};
                result.Add(new{pid=source.Pid,sampled_pages=addresses.Length,inventory_committed_readable_private_pages=census.Item2,inventory_scan_complete=census.Item3,inventory_cap_pages=262144,inventory_capped=census.Item2>addresses.Length,model_included_pages=included.Length,excluded_unknown_or_retained_pages=addresses.Length-included.Length,phases,g5=grouping,model=included.Length>0?EpochModel.Run(trace,1,new[]{10d,20,40,100},10000,durations.ToArray(),ct):new{status="no_complete_page_sequences"}});
            }
            finally{Native.CloseHandle(h);}
        }
        File.WriteAllLines(run.FilePath("epoch-counters.csv"),counters);
        return new{schema="elasticmem.g0.windows_epochs.v2",status="perturbing_epoch_proxy_not_exact_trace",extended,trim_consent=true,system_settings_changed=false,processes=result,incremental_one_gib_decision="unavailable_without_counterfactual_standby_and_compressed_store",limitations=new[]{"Explicitly trims only selected same-session Chromium renderers; may stall user workload; no pagefile or MemoryCompression changes","Valid at epoch end is residency,not all touches; retained-after-trim pages excluded from model,transient return/eviction missed","Extended mode inventories committed readable private addresses; cap262144pages/process,initial universe only; new mappings not added; coverage/exclusions reported","Short mode uses prior resident snapshot; neither mode gives unique physical frames or desktop ceiling,all renderer family not heterogeneous workloads","Fixed baseline→trim→trim+query phase order; uncontrolled workload,CPU/fault counters not task throughput or causal overhead","One-second nominal epochs; query/trim time and scheduling can lengthen actual intervals; model weights measured epoch durations","Virtual allocation reuse at same address not fully detectable; no exact access order or Belady; unknown future selection bias disclosed","Scenario costs10/20/40/100us are assumptions,not measured fault costs; unknown pages excluded,coverage reported","G5 is offline matched pre-trace snapshot intersection; groups frozen on prefix; no churn or measured pagefile faults; raw epochs/pages remain local"}};
    }
}
