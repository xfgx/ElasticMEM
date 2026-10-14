using System.Diagnostics;
using System.Runtime.InteropServices;
using ElasticMem.Core.Backend.Vmm;
using ElasticMem.DriverClient;
namespace ElasticMem.Vmm;
// Actual WHP API implementation, NOT native-tested in this Linux development environment.
// Runs a tiny 16-bit guest, not firmware/Windows. Unknown exits fail closed. No MMIO emulation.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class WhpSyntheticPager
{
    const int Pages=64,FirstData=16,DataPages=16;
    public static object Run(int cycles=32,uint storeCapacity=8192,ulong quotaBytes=16UL*1024*1024,bool injectGetFailure=false)
    {
        if(cycles is <1 or >1024)throw new ArgumentOutOfRangeException(nameof(cycles));
        var availability=WhpNative.Probe();if(!availability.Available)throw new PlatformNotSupportedException(availability.Status+"; no fallback");
        var samples=new List<VmmSnapshot>();samples.Add(VmmMetrics.Capture("before_partition"));
        IntPtr partition=IntPtr.Zero;WhpPageBacking? backing=null;bool vp=false;bool cleanupOk=false;
        using var kernel=new KernelPageStore();kernel.ConfigurePager(storeCapacity,quotaBytes);var capabilities=kernel.Capabilities();
        var store=new ReceiptStore(kernel,injectGetFailure);var barrier=new SingleVpQuiescence(1,false,p=>p>=FirstData&&p<FirstData+DataPages);
        int exits=0,evictions=0,restores=0;var durations=new List<double>();bool failureHeld=false;
        KernelStats? residentStats=null,storedStats=null;object? result=null;
        try
        {
            WhpNative.Check(WhpNative.WHvCreatePartition(out partition));uint count=1;WhpNative.Check(WhpNative.WHvSetPartitionProperty(partition,0x1fff,ref count,4));WhpNative.Check(WhpNative.WHvSetupPartition(partition));
            backing=new(partition,0,Pages);for(int p=0;p<Pages;p++)backing.Map(p);
            byte[] code=new byte[4096];new byte[]{0xA1,0,0,0x40,0xA3,2,0,0xF4}.CopyTo(code,0);backing.Write(0,code);Array.Clear(code);
            for(int p=FirstData;p<FirstData+DataPages;p++){byte[] data=Enumerable.Repeat((byte)(0x31+p-FirstData),4096).ToArray();backing.Write(p,data);Array.Clear(data);}
            WhpNative.Check(WhpNative.WHvCreateVirtualProcessor(partition,0,0));vp=true;
            var provider=new VmPageProvider(Pages,backing,store,barrier);var router=new VmMemoryExitRouter(provider,0,Pages);var policy=new ClockPagePolicy(Pages);
            // Set real mode explicitly. Interrupts disabled; code page pinned. No firmware, disk or OS.
            uint[] regs={0x13,0x15,0x12,0x14,0x16,0x17,0x1c,0x1f,0x10,0x11,0x04};
            var values=new[]{WhpNative.RegisterValue.Segment(0,0x9b),WhpNative.RegisterValue.Segment(0x1000,0x93),WhpNative.RegisterValue.Segment(0,0x93),WhpNative.RegisterValue.Segment(0,0x93),WhpNative.RegisterValue.Segment(0,0x93),WhpNative.RegisterValue.Segment(0,0x93),WhpNative.RegisterValue.Scalar(0x10),WhpNative.RegisterValue.Scalar(0),WhpNative.RegisterValue.Scalar(0),WhpNative.RegisterValue.Scalar(2),WhpNative.RegisterValue.Scalar(0xff0)};
            WhpNative.Check(WhpNative.WHvSetVirtualProcessorRegisters(partition,0,regs,(uint)regs.Length,values));
            void Execute(int page,bool expectFault)
            {
                uint[] reset={0x15,0x10,0x00,0x80000005};var vals=new[]{WhpNative.RegisterValue.Segment((ushort)(page*256),0x93),WhpNative.RegisterValue.Scalar(0),WhpNative.RegisterValue.Scalar(0),WhpNative.RegisterValue.Scalar(0)};
                WhpNative.Check(WhpNative.WHvSetVirtualProcessorRegisters(partition,0,reset,4,vals));int faultsBefore=restores;bool halted=false;
                for(int n=0;n<8;n++)
                {
                    WhpNative.ExitContext exit;int hr;
                    // Cancel only the run, never free memory from a watchdog thread. Join callback before cleanup.
                    int canceled=0;using(var timer=new System.Threading.Timer(_=>{Interlocked.Exchange(ref canceled,1);_ = WhpNative.WHvCancelRunVirtualProcessor(partition,0,0);},null,10000,Timeout.Infinite))
                    {
                        barrier.BeginRun();try{hr=WhpNative.WHvRunVirtualProcessor(partition,0,out exit,224);}finally{barrier.EndRun();}
                        using var joined=new ManualResetEvent(false);timer.Dispose(joined);joined.WaitOne();
                    }
                    WhpNative.Check(hr);if(canceled!=0)throw new TimeoutException("WHP run watchdog expired; guest not resumed");exits++;
                    if(exit.Reason==8){halted=true;break;}
                    if(exit.Reason!=1||!exit.GpaUnmapped)throw new InvalidOperationException("Unhandled WHP exit 0x"+exit.Reason.ToString("X"));
                    // Retry unchanged RIP: the faulting instruction was NOT emulated/advanced.
                    if(!router.RestoreForUnmappedGpa(exit.Gpa))throw new InvalidOperationException("Unowned/resident GPA fault; no zero-page substitution");restores++;policy.ObserveAccess((int)(exit.Gpa/4096));
                }
                if(!halted||expectFault&&restores==faultsBefore)throw new InvalidDataException("Expected guest halt/SYS memory fault missing");
                var rax=new WhpNative.RegisterValue[1];WhpNative.Check(WhpNative.WHvGetVirtualProcessorRegisters(partition,0,new uint[]{0},1,rax));byte pattern=(byte)(0x31+page-FirstData);ushort expected=(ushort)(pattern*257+1);
                if((ushort)rax[0].Value!=expected)throw new InvalidDataException("Guest read register mismatch");
                byte[] data=backing.Read(page);try{if(BitConverter.ToUInt16(data,2)!=expected||data.Where((b,i)=>i!=2&&i!=3).Any(b=>b!=pattern))throw new InvalidDataException("Guest write/full backing mismatch");}finally{Array.Clear(data);}policy.ObserveAccess(page);
            }
            for(int page=FirstData;page<FirstData+DataPages;page++)Execute(page,false); // normalize all synthetic references before A
            samples.Add(VmmMetrics.Capture("resident_before_A"));var a=Stopwatch.StartNew();for(int i=0;i<cycles;i++)Execute(FirstData+i%DataPages,false);a.Stop();residentStats=kernel.Stats();samples.Add(VmmMetrics.Capture("resident_after_A"));
            var b=Stopwatch.StartNew();
            for(int i=0;i<cycles;i++)
            {
                int page=policy.Choose(p=>p>=FirstData&&p<FirstData+DataPages&&provider.State(p)==VmPageState.Resident)??throw new InvalidOperationException("No evictable synthetic page");
                // Both phases use page order FirstData + (i%DataPages); clock hand must agree.
                if(page!=FirstData+i%DataPages)throw new InvalidDataException("A/B synthetic access order mismatch");
                var t=Stopwatch.StartNew();provider.Evict(page);evictions++;
                if(backing.IsMapped(page)||backing.IsCommitted(page)||!backing.IsReserved(page))throw new InvalidDataException("Backing not discarded");
                if(i==0){storedStats=kernel.Stats();samples.Add(VmmMetrics.Capture("stored_backing_decommitted"));}
                if(injectGetFailure)
                {
                    try{Execute(page,true);throw new InvalidDataException("Failure injection did not fail");}
                    catch(IOException)when(barrier.FailedPaused&&provider.State(page)==VmPageState.FailedPaused){failureHeld=true;break;}
                }
                Execute(page,true);t.Stop();durations.Add(t.Elapsed.TotalMilliseconds);
            }
            b.Stop();samples.Add(VmmMetrics.Capture("after_B_before_cleanup"));
            result=new{schema="elasticmem.whp.sys.synthetic.v1",status=failureHeld?"passed_injected_loss_kept_guest_paused":"passed_synthetic_guest_only",backend="custom_WHP_SYS",windows_guest_booted=false,windows_guest_processes_tested=false,vcpu_count=1,device_io_workers=0,guest_pages=Pages,pages_evicted=evictions,pages_restored=restores,whp_exits=exits,cycles,loss_injected=injectGetFailure,failed_guest_resume_blocked=failureHeld,clock_reference_scope="synthetic accesses and restored faults only; NOT complete working set",phase_A_ms=a.Elapsed.TotalMilliseconds,phase_B_ms=b.Elapsed.TotalMilliseconds,pager_roundtrip_ms=durations,snapshots=samples,sys_capabilities=capabilities,sys_resident_ledger=residentStats,sys_stored_ledger=storedStats,physical_ram_saved_bytes=(long?)null,physical_saving_evidence="not_confirmed; snapshots not causal net physical RAM proof",overhead=new{vmm="working_set/private_commit/peak sampled",sys="requested-allocation ledger only; pool rounding excluded",guest_windows="not booted",guest_agent="not running",mstsc="not running",whp_hypervisor="not measured"},baseline_order="A then B, same tiny instruction/data trace; not Windows A/B",migration="not implemented",automatic_backend_fallback=false};
        }
        finally
        {
            // All run calls and watchdog callbacks have returned before teardown. Failed guest is never rerun.
            if(vp)WhpNative.Check(WhpNative.WHvDeleteVirtualProcessor(partition,0));
            try{backing?.Dispose();}
            finally{if(partition!=IntPtr.Zero)WhpNative.Check(WhpNative.WHvDeletePartition(partition));}
            cleanupOk=true;
        }
        if(!cleanupOk||result==null)throw new InvalidOperationException("No confirmed synthetic result");return result;
    }
    sealed class ReceiptStore:IVmCompressedStore
    {
        readonly KernelPageStore kernel;readonly bool failGet;public ReceiptStore(KernelPageStore kernel,bool failGet){this.kernel=kernel;this.failGet=failGet;}
        public VmStoreReceipt Put(uint key,byte[] data){var r=kernel.PutCompressed(key,data);return new(r.Generation,r.PayloadBytes,r.AllocationBytes);}
        public byte[] Get(uint key,ulong generation)=>failGet?throw new IOException("Injected store read loss; real compressed copy remains intact"):kernel.GetVersioned(key,generation);
        public bool Delete(uint key,ulong generation)=>kernel.DeleteVersioned(key,generation);
    }
}
