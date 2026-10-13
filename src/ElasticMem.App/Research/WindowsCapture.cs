using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ElasticMem.Core.Research;
namespace ElasticMem.App.Research;
internal static class WindowsCapture
{
    public static object Sample(int[] pids,int seconds,ResearchRun run,IProgress<string>? progress,CancellationToken ct)
    {
        if(seconds<1||seconds>3600||pids.Length==0)throw new ArgumentException("Invalid duration/PIDs");
        using var csv=new StreamWriter(run.FilePath("process-samples.csv"));csv.WriteLine("elapsed_ms,pid,process_start_utc,working_set_bytes,private_bytes,page_fault_count,cpu_ms,status");
        var sw=Stopwatch.StartNew();long rows=0,unknown=0;var identities=new Dictionary<int,DateTime>();
        while(sw.Elapsed.TotalSeconds<seconds)
        {
            foreach(int pid in pids)
            {
                ct.ThrowIfCancellationRequested();IntPtr h=IntPtr.Zero;
                try
                {
                    using var p=Process.GetProcessById(pid);DateTime start=p.StartTime.ToUniversalTime();
                    if(identities.TryGetValue(pid,out var previous)&&start!=previous)throw new InvalidOperationException("PID reused");identities[pid]=start;
                    h=Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION|Native.PROCESS_VM_READ,false,pid);
                    if(h==IntPtr.Zero||!Native.TryGetMemInfo(h,out var m))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                    if(!Native.GetProcessTimes(h,out var creation,out _,out _,out _)||DateTime.FromFileTimeUtc(creation)!=start)throw new InvalidOperationException("Process instance changed while opening handle");
                    csv.WriteLine(FormattableString.Invariant($"{sw.Elapsed.TotalMilliseconds:F3},{pid},{start:O},{m.WorkingSetSize.ToUInt64()},{m.PrivateUsage.ToUInt64()},{m.PageFaultCount},{p.TotalProcessorTime.TotalMilliseconds:F3},sampled"));rows++;
                }
                catch(Exception ex){unknown++;csv.WriteLine(FormattableString.Invariant($"{sw.Elapsed.TotalMilliseconds:F3},{pid},,,,,,unavailable"));run.Log($"PID {pid}: {ex.GetType().Name}: {ex.Message}");}
                finally{if(h!=IntPtr.Zero)Native.CloseHandle(h);}
            }
            csv.Flush();progress?.Report($"G0 наблюдение: {sw.Elapsed.TotalSeconds:F0}/{seconds} с · ошибок {unknown}");
            if(ct.WaitHandle.WaitOne(250))ct.ThrowIfCancellationRequested();
        }
        if(rows==0)throw new IOException("No readable process counters observed; inspect events.log");
        return new{schema="elasticmem.windows.process_samples.v1",trace_kind="sampled_process_counters",rows,unknown_rows=unknown,elapsed_ms=sw.Elapsed.TotalMilliseconds,
            limitations=new[]{"Working-set/private/page-fault counters, NOT a full page access trace","PageFaultCount includes soft and hard faults; cannot identify SSD traffic","No per-page times, reads/writes or restoration latency; cannot feed G0 exact model","PID instances monitored; exited/denied processes explicitly unavailable","No memory trimming or MMAgent changes performed"}};
    }
    public static ProcessSnapshot Capture(int pid,int maxMiB,ResearchRun run,CancellationToken ct)
    {
        if(maxMiB<1||maxMiB>64)throw new ArgumentException("Snapshot cap 1..64 MiB per process");
        using var p=Process.GetProcessById(pid);long startTicks=p.StartTime.ToUniversalTime().Ticks;
        IntPtr h=Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION|Native.PROCESS_VM_READ,false,pid);if(h==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if(!Native.GetProcessTimes(h,out var creation,out _,out _,out _)||DateTime.FromFileTimeUtc(creation).Ticks!=startTicks)throw new InvalidOperationException("Process instance changed while opening handle");
            if(!Native.IsWow64Process2(h,out var processMachine,out var nativeMachine))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if(processMachine!=0||nativeMachine!=0x8664)throw new NotSupportedException("G1 capture supports native x64 processes only; WOW64/ARM64 rejected");
            var regions=new List<SnapshotRegion>();var pages=new List<SnapshotPage>();ulong cursor=0;long cap=maxMiB*1024L*1024,used=0,failed=0,attempts=0;var started=DateTime.UtcNow;
            while(cursor<0x0000800000000000UL)
            {
                ct.ThrowIfCancellationRequested();if(Native.VirtualQueryEx(h,(IntPtr)(long)cursor,out var mbi,(IntPtr)Marshal.SizeOf<Native.MEMORY_BASIC_INFORMATION>())==IntPtr.Zero)break;
                ulong address=(ulong)mbi.BaseAddress.ToInt64(),size=mbi.RegionSize.ToUInt64();if(size==0||address>ulong.MaxValue-size)break;
                if(mbi.State==Native.MEM_COMMIT&&(mbi.Protect&(Native.PAGE_GUARD|Native.PAGE_NOACCESS))==0&&(mbi.Protect&Native.ReadableMask)!=0)
                {
                    regions.Add(new(address,size));
                    // Only private rw/ro data captured; mapped/image ranges still retained for candidate pointer normalization.
                    if(mbi.Type==Native.MEM_PRIVATE&&used<cap&&attempts<cap/4096*8)
                    {
                        for(ulong off=0;off+4096<=size&&used<cap&&attempts<cap/4096*8;off+=4096)
                        {
                            ct.ThrowIfCancellationRequested();attempts++;var data=new byte[4096];
                            if(Native.ReadProcessMemory(h,(IntPtr)(long)(address+off),data,(IntPtr)4096,out var n)&&n.ToInt64()==4096){pages.Add(new(address+off,data));used+=4096;}
                            else failed++;
                        }
                    }
                }
                ulong next=address+size;if(next<=cursor)break;cursor=next;
            }
            if(p.HasExited||p.StartTime.ToUniversalTime().Ticks!=startTicks)throw new InvalidOperationException("Process lifetime changed during capture");
            if(pages.Count==0)throw new IOException("No readable pages captured");
            string folder=$"snapshots/{pid}";using(var output=File.Create(run.FilePath(folder+"/pages.bin")))foreach(var page in pages)output.Write(page.Bytes);
            run.WriteJson(folder+"/inventory.json",new{pid,start_ticks=startTicks,utc_started=started,utc_finished=DateTime.UtcNow,limit_bytes=cap,captured_bytes=used,unreadable_pages=failed,regions,pages=pages.Select((x,i)=>new{address=x.Address,file_offset=i*4096L}),limitations=new[]{"Bounded non-atomic capture; mappings/contents can change","Address-order sample of private readable pages only","Reading may fault in cold pages; observer changes target working set","No SeDebugPrivilege escalation; protected processes may be unavailable"}});
            return new(pid,startTicks,regions.ToArray(),pages.ToArray());
        }
        finally{Native.CloseHandle(h);}
    }
}
