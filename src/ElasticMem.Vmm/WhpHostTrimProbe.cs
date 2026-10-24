using System.ComponentModel;
using System.Runtime.InteropServices;
namespace ElasticMem.Vmm;

// Question this answers natively: can the Windows memory manager already trim (and compress/page) memory that
// is mapped into a WHP partition, without any custom pager? If yes, a custom pager duplicates the host OS.
// Method: guest touches all data pages -> QueryWorkingSetEx on the backing VA -> EmptyWorkingSet(self) ->
// QueryWorkingSetEx again -> guest re-reads every page and results are verified -> QueryWorkingSetEx again.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class WhpHostTrimProbe
{
    [StructLayout(LayoutKind.Sequential)]struct WorkingSetExInformation{public IntPtr VirtualAddress;public nuint Attributes;}
    [DllImport("kernel32.dll",SetLastError=true,EntryPoint="K32QueryWorkingSetEx")][return:MarshalAs(UnmanagedType.Bool)]static extern bool QueryWorkingSetEx(IntPtr process,[In,Out]WorkingSetExInformation[] info,uint size);
    [DllImport("kernel32.dll",SetLastError=true,EntryPoint="K32EmptyWorkingSet")][return:MarshalAs(UnmanagedType.Bool)]static extern bool EmptyWorkingSet(IntPtr process);
    [DllImport("kernel32.dll")]static extern IntPtr GetCurrentProcess();

    public sealed record Residency(int Pages,int Valid,int Locked,int Shared);
    static Residency Query(WhpPageBacking backing,int first,int count)
    {
        var info=new WorkingSetExInformation[count];for(int i=0;i<count;i++)info[i].VirtualAddress=backing.HostAddress(first+i);
        if(!QueryWorkingSetEx(GetCurrentProcess(),info,(uint)(count*Marshal.SizeOf<WorkingSetExInformation>())))throw new Win32Exception(Marshal.GetLastWin32Error());
        int valid=0,locked=0,shared=0;
        foreach(var e in info){ulong a=e.Attributes;if((a&1)!=0){valid++;if((a&(1UL<<15))!=0)shared++;if((a&(1UL<<22))!=0)locked++;}}
        return new(count,valid,locked,shared);
    }

    public static object Run(int rounds=3)
    {
        if(rounds is <1 or >32)throw new ArgumentOutOfRangeException(nameof(rounds));
        var availability=WhpNative.Probe();
        if(!availability.Available)throw new VmmStageException("whp_probe",new PlatformNotSupportedException(availability.Status));
        var samples=new List<VmmSnapshot>{VmmMetrics.Capture("before_partition")};var rows=new List<object>();
        using var vm=WhpSyntheticVm.Create();
        const int first=WhpSyntheticVm.FirstData,count=WhpSyntheticVm.DataPages;
        bool allCorrect=true;int trimmedTotal=0;
        for(int r=0;r<rounds;r++)
        {
            for(int p=first;p<first+count;p++)vm.Execute(p,null);
            var touched=vm.At("wsex_query",()=>Query(vm.Backing,first,count));
            vm.At("empty_working_set",()=>{if(!EmptyWorkingSet(GetCurrentProcess()))throw new Win32Exception(Marshal.GetLastWin32Error());});
            var trimmed=vm.At("wsex_query",()=>Query(vm.Backing,first,count));
            samples.Add(VmmMetrics.Capture("after_trim_round_"+r));
            // Any mismatch throws from Execute (guest_verify). Unmapped/unknown exits throw: a trimmed page must be
            // transparently faulted back by the host/hypervisor without a WHP exit to us.
            for(int p=first;p<first+count;p++)vm.Execute(p,null);
            var reaccessed=vm.At("wsex_query",()=>Query(vm.Backing,first,count));
            trimmedTotal+=touched.Valid-trimmed.Valid;
            rows.Add(new{round=r,resident_after_guest_touch=touched,resident_after_empty_working_set=trimmed,resident_after_guest_reaccess=reaccessed});
        }
        int exits=vm.Exits;vm.Dispose();
        if(vm.TeardownErrors.Count>0)throw new VmmStageException("teardown",new InvalidOperationException(string.Join(";",vm.TeardownErrors)));
        string verdict=trimmedTotal>0?"host_can_trim_whp_mapped_memory":"whp_mapped_memory_not_trimmed_by_empty_working_set";
        return new{schema="elasticmem.whp.host-trim.v1",status="passed_measurement",backend="custom_WHP_host_trim_probe",whp=availability,windows_guest_booted=false,
            guest_pages_probed=count,rounds,whp_exits=exits,guest_results_correct_after_trim=allCorrect,pages_trimmed_total=trimmedTotal,verdict,rows,snapshots=samples,
            interpretation=trimmedTotal>0
                ?"Windows memory manager already removes WHP-mapped pages from the VMM working set and the guest still reads correct data. Trimmed private pages go to the modified/standby lists and Memory Compression like any process memory; a custom pager must beat that, not merely do it."
                :"Pages stayed resident (likely locked/pinned by WHP on this build). A custom pager is the only way to release them; re-check on Windows 11.",
            physical_ram_saved_bytes=(long?)null,automatic_backend_fallback=false};
    }
}
