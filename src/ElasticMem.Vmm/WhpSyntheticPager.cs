using System.Diagnostics;
using ElasticMem.Core.Backend.Vmm;
using ElasticMem.DriverClient;
namespace ElasticMem.Vmm;

public enum PagerStoreKind { Sys, User }

// WHP synthetic guest + pager: evict (PUT -> unmap -> decommit), GpaUnmapped exit -> GET -> commit/write/map -> retry.
// Not firmware/Windows. Unknown exits fail closed, no zero-page substitution, no Hyper-V VM fallback.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class WhpSyntheticPager
{
    const int Pages=WhpSyntheticVm.Pages,FirstData=WhpSyntheticVm.FirstData,DataPages=WhpSyntheticVm.DataPages;
    public static string BackendName(PagerStoreKind kind)=>kind==PagerStoreKind.Sys?"custom_WHP_SYS":"custom_WHP_user_store";

    public static object Run(int cycles=32,PagerStoreKind storeKind=PagerStoreKind.Sys,uint storeCapacity=8192,ulong quotaBytes=16UL*1024*1024,bool injectGetFailure=false)
    {
        if(cycles is <1 or >1024)throw new ArgumentOutOfRangeException(nameof(cycles));
        var availability=WhpNative.Probe();
        if(!availability.Available)throw new VmmStageException("whp_probe",new PlatformNotSupportedException(availability.Status));
        var samples=new List<VmmSnapshot>{VmmMetrics.Capture("before_partition")};
        KernelPageStore? kernel=null;NtXpressCodec? codec=null;UserPageStore? user=null;object? capabilities=null;
        string stage="store_open";
        try
        {
            if(storeKind==PagerStoreKind.Sys)
            {
                try{kernel=new KernelPageStore();}catch(Exception ex){throw new VmmStageException("sys_open",ex);}
                try{kernel.ConfigurePager(storeCapacity,quotaBytes);capabilities=kernel.Capabilities();}catch(Exception ex){throw new VmmStageException("sys_configure",ex);}
            }
            else
            {
                try{codec=new NtXpressCodec();user=new UserPageStore(codec,Pages,(long)quotaBytes);capabilities=new{codec=codec.Name,capacity=Pages,quota_bytes=quotaBytes};}catch(Exception ex){throw new VmmStageException("user_store_open",ex);}
            }
            IVmCompressedStore inner=storeKind==PagerStoreKind.Sys?new KernelStore(kernel!):user!;
            var store=new FaultInjectingStore(inner,injectGetFailure);
            object? Ledger()=>kernel!=null?kernel.Stats():user!.Stats();
            stage="vm";
            using var vm=WhpSyntheticVm.Create();
            var barrier=new SingleVpQuiescence(1,false,p=>p>=FirstData&&p<FirstData+DataPages);
            var provider=new VmPageProvider(Pages,vm.Backing,store,barrier);var router=new VmMemoryExitRouter(provider,0,Pages);var policy=new ClockPagePolicy(Pages);
            int evictions=0,restores=0;var durations=new List<double>();bool failureHeld=false;object? residentLedger=null,storedLedger=null;
            int Exec(int page)
            {
                int f=vm.Execute(page,gpa=>{bool ok=router.RestoreForUnmappedGpa(gpa);if(ok){restores++;policy.ObserveAccess((int)(gpa/4096));}return ok;},barrier.BeginRun,barrier.EndRun);
                policy.ObserveAccess(page);return f;
            }
            for(int page=FirstData;page<FirstData+DataPages;page++)Exec(page); // normalize references before A
            samples.Add(VmmMetrics.Capture("resident_before_A"));
            var a=Stopwatch.StartNew();for(int i=0;i<cycles;i++)Exec(FirstData+i%DataPages);a.Stop();
            residentLedger=Ledger();samples.Add(VmmMetrics.Capture("resident_after_A"));
            var b=Stopwatch.StartNew();
            for(int i=0;i<cycles;i++)
            {
                int page=policy.Choose(p=>p>=FirstData&&p<FirstData+DataPages&&provider.State(p)==VmPageState.Resident)??throw new VmmStageException("pager_policy",new InvalidOperationException("No evictable synthetic page"));
                if(page!=FirstData+i%DataPages)throw new VmmStageException("pager_policy",new InvalidDataException("A/B synthetic access order mismatch"));
                var t=Stopwatch.StartNew();
                vm.At("pager_evict",()=>provider.Evict(page));evictions++;
                if(vm.Backing.IsMapped(page)||vm.Backing.IsCommitted(page)||!vm.Backing.IsReserved(page))throw new VmmStageException("pager_evict",new InvalidDataException("Backing not discarded"));
                if(i==0){storedLedger=Ledger();samples.Add(VmmMetrics.Capture("stored_backing_decommitted"));}
                if(injectGetFailure)
                {
                    try{Exec(page);throw new VmmStageException("inject",new InvalidDataException("Failure injection did not fail"));}
                    catch(VmmStageException ex)when(ex.Stage=="pager_restore"&&ex.InnerException is IOException&&barrier.FailedPaused&&provider.State(page)==VmPageState.FailedPaused){failureHeld=true;break;}
                }
                if(Exec(page)<1)throw new VmmStageException("guest_verify",new InvalidDataException("Expected SYS memory fault missing"));
                t.Stop();durations.Add(t.Elapsed.TotalMilliseconds);
            }
            b.Stop();samples.Add(VmmMetrics.Capture("after_B_before_cleanup"));
            int exits=vm.Exits;
            vm.Dispose();
            if(vm.TeardownErrors.Count>0)throw new VmmStageException("teardown",new InvalidOperationException(string.Join(";",vm.TeardownErrors)));
            samples.Add(VmmMetrics.Capture("after_teardown"));
            durations.Sort();
            double P(double q)=>durations.Count==0?double.NaN:durations[Math.Min(durations.Count-1,(int)Math.Ceiling(q*durations.Count)-1)];
            return new{schema="elasticmem.whp.pager.synthetic.v2",status=failureHeld?"passed_injected_loss_kept_guest_paused":"passed_synthetic_guest_only",backend=BackendName(storeKind),
                store_backend=storeKind==PagerStoreKind.Sys?"sys_kernel_xpress_paged_pool":"user_ntdll_xpress_process_heap",whp=availability,
                windows_guest_booted=false,windows_guest_processes_tested=false,vcpu_count=1,device_io_workers=0,guest_pages=Pages,pages_evicted=evictions,pages_restored=restores,whp_exits=exits,cycles,
                loss_injected=injectGetFailure,failed_guest_resume_blocked=failureHeld,clock_reference_scope="synthetic accesses and restored faults only; NOT complete working set",
                phase_A_ms=a.Elapsed.TotalMilliseconds,phase_B_ms=b.Elapsed.TotalMilliseconds,pager_roundtrip_ms=new{count=durations.Count,p50=P(0.5),p95=P(0.95),max=durations.Count==0?double.NaN:durations[^1],all=durations},
                snapshots=samples,store_capabilities=capabilities,store_resident_ledger=residentLedger,store_stored_ledger=storedLedger,
                physical_ram_saved_bytes=(long?)null,physical_saving_evidence="not_confirmed; 64-page synthetic guest is below measurement noise; compare system_commit/available, not process RSS",
                overhead=new{vmm="process working_set/private_commit/peak sampled",store=storeKind==PagerStoreKind.Sys?"SYS ledger (paged pool, not in process RSS; see kernel_paged_pool in snapshots)":"user ledger (process heap, included in private commit)",guest_windows="not booted",guest_agent="not running",mstsc="not running",whp_hypervisor="not measured"},
                baseline_order="A then B, same tiny instruction/data trace; not Windows A/B",migration="not implemented",automatic_backend_fallback=false};
        }
        catch(VmmStageException){throw;}
        catch(Exception ex){throw new VmmStageException(stage,ex);}
        finally{kernel?.Dispose();user?.Dispose();codec?.Dispose();}
    }

    sealed class KernelStore:IVmCompressedStore
    {
        readonly KernelPageStore kernel;public KernelStore(KernelPageStore kernel){this.kernel=kernel;}
        public VmStoreReceipt Put(uint key,byte[] data){var r=kernel.PutCompressed(key,data);return new(r.Generation,r.PayloadBytes,r.AllocationBytes);}
        public byte[] Get(uint key,ulong generation)=>kernel.GetVersioned(key,generation);
        public bool Delete(uint key,ulong generation)=>kernel.DeleteVersioned(key,generation);
    }
    sealed class FaultInjectingStore:IVmCompressedStore
    {
        readonly IVmCompressedStore inner;readonly bool failGet;public FaultInjectingStore(IVmCompressedStore inner,bool failGet){this.inner=inner;this.failGet=failGet;}
        public VmStoreReceipt Put(uint key,byte[] data)=>inner.Put(key,data);
        public byte[] Get(uint key,ulong generation)=>failGet?throw new IOException("Injected store read loss; real compressed copy remains intact"):inner.Get(key,generation);
        public bool Delete(uint key,ulong generation)=>inner.Delete(key,generation);
    }
}
