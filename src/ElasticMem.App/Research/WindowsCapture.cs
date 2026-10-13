using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using ElasticMem.Core.Research;
namespace ElasticMem.App.Research;
internal static class WindowsCapture
{
    static string Csv(string text)=>"\""+text.Replace("\"","\"\"")+"\"";
    public static object Sample(int[] pids,int seconds,ResearchRun run,IProgress<string>? progress,CancellationToken ct)
    {
        if(seconds<1||seconds>3600||pids.Length==0)throw new ArgumentException("Invalid duration/PIDs");
        using var csv=new StreamWriter(run.FilePath("process-samples.csv"));csv.WriteLine("elapsed_ms,pid,process_name,process_start_utc,working_set_bytes,private_bytes,page_fault_count,cpu_ms,status");
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
                    csv.WriteLine(FormattableString.Invariant($"{sw.Elapsed.TotalMilliseconds:F3},{pid},{Csv(p.ProcessName)},{start:O},{m.WorkingSetSize.ToUInt64()},{m.PrivateUsage.ToUInt64()},{m.PageFaultCount},{p.TotalProcessorTime.TotalMilliseconds:F3},sampled"));rows++;
                }
                catch(Exception ex){unknown++;csv.WriteLine(FormattableString.Invariant($"{sw.Elapsed.TotalMilliseconds:F3},{pid},,,,,,,unavailable"));run.Log($"PID {pid}: {ex.GetType().Name}: {ex.Message}");}
                finally{if(h!=IntPtr.Zero)Native.CloseHandle(h);}
            }
            csv.Flush();progress?.Report($"G0 наблюдение: {sw.Elapsed.TotalSeconds:F0}/{seconds} с · ошибок {unknown}");
            if(ct.WaitHandle.WaitOne(250))ct.ThrowIfCancellationRequested();
        }
        if(rows==0)throw new IOException("No readable process counters observed; inspect events.log");
        return new{schema="elasticmem.windows.process_samples.v1",trace_kind="sampled_process_counters",rows,unknown_rows=unknown,elapsed_ms=sw.Elapsed.TotalMilliseconds,
            limitations=new[]{"Working-set/private/page-fault counters, NOT a full page access trace","PageFaultCount includes soft and hard faults; cannot identify SSD traffic","No per-page times, reads/writes or restoration latency; cannot feed G0 exact model","PID instances monitored; exited/denied processes explicitly unavailable","No memory trimming or MMAgent changes performed"}};
    }
    [StructLayout(LayoutKind.Sequential)]
    struct WorkingSetInfo { public IntPtr Address; public UIntPtr Flags; }
    [DllImport("kernel32.dll", EntryPoint="K32QueryWorkingSetEx", SetLastError=true)]
    static extern bool QueryWorkingSet(IntPtr process, [In, Out] WorkingSetInfo[] information, uint bytes);
    static PageResidency[] Query(IntPtr process, ulong[] addresses)
    {
        var info=addresses.Select(a=>new WorkingSetInfo{Address=(IntPtr)(long)a}).ToArray();
        if(!QueryWorkingSet(process,info,checked((uint)(info.Length*Marshal.SizeOf<WorkingSetInfo>()))))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"QueryWorkingSetEx failed; no residency guessed");
        return info.Select(x=>PageResidency.Decode(x.Flags.ToUInt64())).ToArray();
    }
    public static ProcessSnapshot Capture(int pid,int maxMiB,ResearchRun run,CancellationToken ct,long? expectedStartTicks=null)
    {
        if(pid==Environment.ProcessId)throw new ArgumentException("G1 observer self-capture is excluded: capture buffers contaminate page contents. Select external application PIDs.");
        if(pid<=4)throw new ArgumentException("Select a normal application PID, not Idle/System");
        if(maxMiB<1||maxMiB>64)throw new ArgumentException("Snapshot cap 1..64 MiB per process");
        using var p=Process.GetProcessById(pid);long startTicks=p.StartTime.ToUniversalTime().Ticks;string processName=p.ProcessName;
        if(expectedStartTicks.HasValue&&startTicks!=expectedStartTicks.Value)throw new InvalidOperationException("Selected process instance changed; PID reuse rejected before reading");
        IntPtr h=Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION|Native.PROCESS_VM_READ,false,pid);
        if(h==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if(!Native.GetProcessTimes(h,out var creation,out _,out _,out _)||DateTime.FromFileTimeUtc(creation).Ticks!=startTicks)throw new InvalidOperationException("Process instance changed while opening handle");
            if(!Native.IsWow64Process2(h,out var processMachine,out var nativeMachine))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if(processMachine!=0||nativeMachine!=0x8664)throw new NotSupportedException("G1 capture supports native x64 processes only; WOW64/ARM64 rejected");
            var regions=new List<SnapshotRegion>();var pages=new List<SnapshotPage>();var pageMetadata=new List<object>();
            var classes=new Dictionary<string,long>();void Count(string key){classes[key]=classes.GetValueOrDefault(key)+1;}
            ulong cursor=0;long cap=maxMiB*1024L*1024,used=0,failed=0,queried=0,zeroPages=0,races=0;
            const long queryCap=262144;var clock=Stopwatch.StartNew();var started=DateTime.UtcNow;
            bool beforeCountersValid=Native.TryGetMemInfo(h,out var memBefore);string folder=$"snapshots/{pid}";
            using var status=new StreamWriter(run.FilePath(folder+"/page-status.csv"));
            status.WriteLine("address,region_type,region_base,valid_pre,shared_pre,share_count_pre,flags_pre,valid_post,shared_post,share_count_post,flags_post,decision,zero_if_captured");
            string B(bool? b)=>b.HasValue?(b.Value?"true":"false"):"";
            void Row(ulong a,uint type,ulong region,PageResidency pre,PageResidency? post,string decision,bool? zero=null)
                =>status.WriteLine(FormattableString.Invariant($"{a},{PageEligibility.RegionName(type)},{region},{B(pre.Valid)},{B(pre.Shareable)},{pre.ShareCount},{pre.Flags},{B(post?.Valid)},{B(post?.Shareable)},{post?.ShareCount},{post?.Flags},{decision},{B(zero)}"));
            while(cursor<0x0000800000000000UL)
            {
                ct.ThrowIfCancellationRequested();if(Native.VirtualQueryEx(h,(IntPtr)(long)cursor,out var mbi,(IntPtr)Marshal.SizeOf<Native.MEMORY_BASIC_INFORMATION>())==IntPtr.Zero)break;
                ulong address=(ulong)mbi.BaseAddress.ToInt64(),size=mbi.RegionSize.ToUInt64();if(size==0||address>ulong.MaxValue-size)break;
                if(mbi.State==Native.MEM_COMMIT&&(mbi.Protect&(Native.PAGE_GUARD|Native.PAGE_NOACCESS))==0&&(mbi.Protect&Native.ReadableMask)!=0)
                {
                    regions.Add(new(address,size));
                    // Query all region types in the bounded prefix; skip pages classified as invalid/image/mapped/shareable by prechecks; races remain.
                    for(ulong off=0;off+4096<=size&&used<cap&&queried<queryCap&&clock.Elapsed.TotalSeconds<60;)
                    {
                        ct.ThrowIfCancellationRequested();int count=(int)Math.Min(1024UL,(size-off)/4096);count=(int)Math.Min(count,queryCap-queried);
                        ulong[] addresses=Enumerable.Range(0,count).Select(i=>address+off+(ulong)i*4096).ToArray();
                        var states=Query(h,addresses);queried+=count;off+=(ulong)count*4096;
                        for(int i=0;i<count;i++)
                        {
                            ct.ThrowIfCancellationRequested();if(clock.Elapsed.TotalSeconds>=60)break;ulong a=addresses[i];var pre=states[i];string category=PageEligibility.Classify(mbi.Type,pre);Count(category);
                            if(!PageEligibility.Eligible(mbi.Type,pre)){Row(a,mbi.Type,address,pre,null,category);continue;}
                            if(used>=cap){Count("eligible_not_read_cap");Row(a,mbi.Type,address,pre,null,"eligible_not_read_cap");continue;}
                            // Narrow the pre-query/read race; no atomic snapshot or zero observer effect is claimed.
                            var immediate=Query(h,new[]{a})[0];
                            bool mapping=Native.VirtualQueryEx(h,(IntPtr)(long)a,out var now,(IntPtr)Marshal.SizeOf<Native.MEMORY_BASIC_INFORMATION>())!=IntPtr.Zero
                                &&now.State==Native.MEM_COMMIT&&now.Type==PageEligibility.Private&&now.BaseAddress==mbi.BaseAddress&&now.AllocationBase==mbi.AllocationBase
                                &&(now.Protect&(Native.PAGE_GUARD|Native.PAGE_NOACCESS))==0&&(now.Protect&Native.ReadableMask)!=0;
                            if(!mapping||!PageEligibility.Eligible(now.Type,immediate)){races++;Count("changed_before_read");Row(a,mbi.Type,address,pre,immediate,"changed_before_read");continue;}
                            var data=new byte[4096];bool read=Native.ReadProcessMemory(h,(IntPtr)(long)a,data,(IntPtr)4096,out var n)&&n.ToInt64()==4096;
                            var post=Query(h,new[]{a})[0];
                            if(!read){failed++;Count("read_failed");Row(a,mbi.Type,address,pre,post,"read_failed");continue;}
                            bool stable=Native.VirtualQueryEx(h,(IntPtr)(long)a,out var after,(IntPtr)Marshal.SizeOf<Native.MEMORY_BASIC_INFORMATION>())!=IntPtr.Zero
                                &&after.State==Native.MEM_COMMIT&&after.Type==PageEligibility.Private&&after.BaseAddress==now.BaseAddress&&after.AllocationBase==now.AllocationBase;
                            if(!stable||!PageEligibility.Eligible(after.Type,post)){races++;Count("changed_after_read_excluded");Row(a,mbi.Type,address,pre,post,"changed_after_read_excluded");continue;}
                            bool zero=data.AsSpan().IndexOfAnyExcept((byte)0)<0;if(zero)zeroPages++;
                            pages.Add(new(a,data));pageMetadata.Add(new{address=a,file_offset=used,region_type="MEM_PRIVATE",region_base=address,valid_pre=pre.Valid,shared_pre=pre.Shareable,share_count_pre=pre.ShareCount,flags_pre=pre.Flags,valid_immediate=immediate.Valid,shared_immediate=immediate.Shareable,share_count_immediate=immediate.ShareCount,valid_post=post.Valid,shared_post=post.Shareable,share_count_post=post.ShareCount,flags_post=post.Flags,zero});
                            used+=4096;Count("captured_resident_private_exclusive");Row(a,mbi.Type,address,pre,post,"captured",zero);
                        }
                    }
                }
                ulong next=address+size;if(next<=cursor)break;cursor=next;
            }
            status.Flush();bool afterCountersValid=Native.TryGetMemInfo(h,out var memAfter);
            if(p.HasExited||p.StartTime.ToUniversalTime().Ticks!=startTicks)throw new InvalidOperationException("Process lifetime changed during capture");
            string[] limits={"Bounded address-order, non-atomic sample; not a full census or random sample","QueryWorkingSetEx before and after reading; residency can still change between calls; no guarantee of zero added faults","Process fault/working-set deltas include concurrent workload and cannot be attributed solely to the dumper","Valid=0 pages are never deliberately read; excluded contents are unknown, not assumed zero; Valid=0 does not distinguish demand-zero/standby/compressed/swapped or prove absence from physical RAM","Shared means shareable, not proof of current multi-process physical sharing; ShareCount saturates at 7","MEM_IMAGE, MEM_MAPPED and shareable/shared private pages counted separately and excluded from primary compression input","Private exclusive virtual pages are not a measurement of unique physical RSS","No SeDebugPrivilege escalation, memory trimming, VirtualLock, page combining or Windows compression changes"};
            var summary=new{pid,process_name=processName,start_ticks=startTicks,utc_started=started,utc_finished=DateTime.UtcNow,limit_bytes=cap,captured_bytes=used,captured_zero_pages=zeroPages,unreadable_pages=failed,residency_races=races,batch_queried_pages=queried,query_page_cap=queryCap,page_sampling_deadline_seconds=60,stop_reason=used>=cap?"byte_cap":queried>=queryCap?"query_page_cap":clock.Elapsed.TotalSeconds>=60?"sampling_deadline":"address_scan_end",region_catalog_continues_after_sampling_stop=true,coverage="bounded_prefix_not_full_process",classification_counts=classes,working_set_before=beforeCountersValid?(ulong?)memBefore.WorkingSetSize.ToUInt64():null,working_set_after=afterCountersValid?(ulong?)memAfter.WorkingSetSize.ToUInt64():null,page_fault_count_before=beforeCountersValid?(uint?)memBefore.PageFaultCount:null,page_fault_count_after=afterCountersValid?(uint?)memAfter.PageFaultCount:null,limitations=limits};
            // Safe aggregate included in logs-only exports; page-level addresses stay in sensitive snapshots/.
            run.WriteJson($"residency-summary-{pid}.json",summary);
            using(var output=File.Create(run.FilePath(folder+"/pages.bin")))foreach(var page in pages)output.Write(page.Bytes);
            run.WriteJson(folder+"/inventory.json",new{schema="elasticmem.resident_snapshot.v2",pid,process_name=processName,start_ticks=startTicks,utc_started=started,utc_finished=DateTime.UtcNow,limit_bytes=cap,captured_bytes=used,unreadable_pages=failed,captured_zero_pages=zeroPages,regions,pages=pageMetadata,limitations=limits});
            if(pages.Count==0)throw new IOException($"PID {pid} ({processName}): no eligible resident private exclusive pages; inspect residency-summary-{pid}.json");
            return new(pid,startTicks,regions.ToArray(),pages.ToArray());
        }
        finally{Native.CloseHandle(h);}
    }
}
