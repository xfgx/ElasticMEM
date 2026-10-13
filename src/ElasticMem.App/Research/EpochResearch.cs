using System.Diagnostics;
using System.Runtime.InteropServices;
using ElasticMem.Core.Research;
namespace ElasticMem.App.Research;
internal static class EpochResearch
{
    [StructLayout(LayoutKind.Sequential)]struct WS {public IntPtr Address;public UIntPtr Flags;}
    [DllImport("kernel32.dll",EntryPoint="K32QueryWorkingSetEx",SetLastError=true)]static extern bool QueryWorkingSet(IntPtr h,[In,Out]WS[] pages,uint length);
    [DllImport("psapi.dll",SetLastError=true)]static extern bool EmptyWorkingSet(IntPtr h);
    static bool[] Valid(IntPtr h,ulong[] addresses){var p=addresses.Select(a=>new WS{Address=(IntPtr)(long)a}).ToArray();if(!QueryWorkingSet(h,p,(uint)(p.Length*Marshal.SizeOf<WS>())))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());return p.Select(a=>(a.Flags.ToUInt64()&1)!=0).ToArray();}
    static ulong[] Allocations(IntPtr h,ulong[] addresses){return addresses.Select(a=>Native.VirtualQueryEx(h,(IntPtr)(long)a,out var m,(IntPtr)Marshal.SizeOf<Native.MEMORY_BASIC_INFORMATION>())!=IntPtr.Zero&&m.State==Native.MEM_COMMIT&&m.Type==PageEligibility.Private&&PageMapping.Contains((ulong)m.BaseAddress.ToInt64(),m.RegionSize.ToUInt64(),a,4096)?(ulong)m.AllocationBase.ToInt64():0UL).ToArray();}
    public static object Run(CodecInput[] input,bool allowTrim,int epochs,ResearchRun run,IProgress<string>? progress,CancellationToken ct)
    {
        if(!allowTrim)throw new ArgumentException("Explicit --allow-working-set-trim required; never disable pagefile/compression");if(epochs is <4 or >60)throw new ArgumentException("Epoch count4..60");var result=new List<object>();var counters=new List<string>{"pid,phase,epoch,elapsed_ms,working_set_bytes,page_fault_count,retained_after_trim,valid_at_end,observed_reacquired,trim_call_us"};
        foreach(var source in input.Take(2))
        {
            ct.ThrowIfCancellationRequested();if(source.Pid<=4||source.Pid==Environment.ProcessId)throw new ArgumentException("Foreign ordinary application only");
            using var p=Process.GetProcessById(source.Pid);if(p.ProcessName is not("chrome" or "msedge" or "steamwebhelper")||p.SessionId!=Process.GetCurrentProcess().SessionId||p.StartTime.ToUniversalTime().Ticks!=source.StartTicks)throw new InvalidOperationException("Target identity/session changed");
            IntPtr h=Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION|Native.PROCESS_VM_READ|0x100,false,source.Pid);if(h==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if(!Native.GetProcessTimes(h,out var creation,out var exit,out _,out _)||exit!=0||DateTime.FromFileTimeUtc(creation).Ticks!=source.StartTicks)throw new InvalidOperationException("Handle identity changed");
                AutoResearch.ConfirmRenderer(h,source.Pid,p.ProcessName,source.StartTicks);
                // Spread over captured pages only; do not claim a stratified full-process sample.
                var addresses=Enumerable.Range(0,Math.Min(source.Addresses.Length,1024)).Select(i=>source.Addresses[i*source.Addresses.Length/Math.Min(source.Addresses.Length,1024)]).ToArray();var original=Allocations(h,addresses);var allKnown=original.Select(x=>x!=0).ToArray();var activity=new List<bool[]>();var durations=new List<double>();var phases=new List<object>();
                foreach(string phase in new[]{"baseline_counters","trim_counters","trim_and_residency"})
                {
                    if(!Native.TryGetMemInfo(h,out var before))throw new IOException("Missing pre-phase counters");Native.GetProcessTimes(h,out _,out _,out var kernelBefore,out var userBefore);var sw=Stopwatch.StartNew();int actual=phase=="trim_and_residency"?epochs:4;long faultsBefore=before.PageFaultCount;double trimTotal=0;
                    for(int i=0;i<actual;i++)
                    {
                        ct.ThrowIfCancellationRequested();progress?.Report($"Г0 эпохи: PID {source.Pid} {phase} {i+1}/{actual}");double trimUs=0;bool[]? after=null;var timer=Stopwatch.StartNew();if(phase!="baseline_counters"){AutoResearch.ConfirmRenderer(h,source.Pid,p.ProcessName,source.StartTicks);long t=Stopwatch.GetTimestamp();if(!EmptyWorkingSet(h))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"EmptyWorkingSet failed");trimUs=(Stopwatch.GetTimestamp()-t)*1e6/Stopwatch.Frequency;trimTotal+=trimUs;}
                        if(phase=="trim_and_residency")after=Valid(h,addresses);
                        int wait=Math.Max(0,1000-(int)timer.ElapsedMilliseconds);if(ct.WaitHandle.WaitOne(wait))ct.ThrowIfCancellationRequested();
                        int retained=0,endCount=0,reacquired=0;if(after!=null){var end=Valid(h,addresses);var allocation=Allocations(h,addresses);var observed=new bool[addresses.Length];for(int j=0;j<observed.Length;j++){bool known=original[j]!=0&&allocation[j]==original[j]&&!after[j];allKnown[j]&=known;observed[j]=known&&end[j];if(after[j])retained++;if(end[j])endCount++;if(observed[j])reacquired++;}activity.Add(observed);durations.Add(timer.Elapsed.TotalSeconds);}
                        if(!Native.TryGetMemInfo(h,out var c))throw new IOException("Missing epoch counters");counters.Add(FormattableString.Invariant($"{source.Pid},{phase},{i},{sw.Elapsed.TotalMilliseconds:F3},{c.WorkingSetSize.ToUInt64()},{c.PageFaultCount},{retained},{endCount},{reacquired},{trimUs:F3}"));
                        if(!Native.GetProcessTimes(h,out var now,out var stopped,out _,out _)||now!=creation||stopped!=0)throw new InvalidOperationException("Process exited during epochs");
                    }
                    if(!Native.TryGetMemInfo(h,out var last))throw new IOException("Missing post-phase counters");Native.GetProcessTimes(h,out _,out _,out var kernelAfter,out var userAfter);phases.Add(new{phase,epochs=actual,elapsed_ms=sw.Elapsed.TotalMilliseconds,fault_delta=(long)last.PageFaultCount-faultsBefore,cpu_ms=((kernelAfter-kernelBefore)+(userAfter-userBefore))/10000d,working_set_before=before.WorkingSetSize.ToUInt64(),working_set_after=last.WorkingSetSize.ToUInt64(),trim_call_total_us=trimTotal,workload_throughput_measured=false});
                }
                int[] included=Enumerable.Range(0,allKnown.Length).Where(i=>allKnown[i]).ToArray();var trace=activity.Select(a=>included.Select(i=>a[i]).ToArray()).ToArray();run.WriteJson($"inputs/epochs-{source.Pid}.json",new{pid=source.Pid,source.StartTicks,addresses,original_allocation=original,known_all_epochs=allKnown,activity});
                result.Add(new{pid=source.Pid,sampled_pages=addresses.Length,model_included_pages=included.Length,phases,model=included.Length>0?EpochModel.Run(trace,1,new[]{10d,20,40,100},10000,durations.ToArray()):new{status="no_complete_page_sequences"}});
            }
            finally{Native.CloseHandle(h);}
        }
        File.WriteAllLines(run.FilePath("epoch-counters.csv"),counters);
        return new{schema="elasticmem.g0.windows_epochs.v1",status="perturbing_epoch_proxy_not_exact_trace",trim_consent=true,system_settings_changed=false,processes=result,limitations=new[]{"Explicitly trims only selected same-session Chromium processes; may stall user workload; no pagefile or MemoryCompression changes","Valid at epoch end is residency,not all touches; retained-after-trim pages excluded from model,transient return/eviction missed","Sample spread over prior bounded resident snapshot; not full committed ranges or desktop ceiling","Fixed baseline→trim→trim+query phase order; uncontrolled workload,CPU/fault counters not task throughput or causal overhead","One-second nominal epochs; query/trim time and scheduling can lengthen actual intervals; model weights measured epoch durations","Virtual allocation reuse at same address not fully detectable; no exact access order or Belady","Scenario costs10/20/40/100us are assumptions,not measured fault costs; unknown pages excluded,coverage reported"}};
    }
}
