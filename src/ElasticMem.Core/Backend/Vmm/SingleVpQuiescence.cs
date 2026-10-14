namespace ElasticMem.Core.Backend.Vmm;
// This is NOT a multi-vCPU/DMA stop-the-world barrier. It explicitly rejects that topology.
// All run, page operations and emulated accesses are on one owner thread. No device/I/O worker exists.
public sealed class SingleVpQuiescence : IVmQuiescence
{
    readonly int owner=Environment.CurrentManagedThreadId;readonly Func<int,bool> evictable;
    bool running,failed;int leases;
    public SingleVpQuiescence(int vcpus,bool hasIoWorkers,Func<int,bool> evictable)
    {if(vcpus!=1||hasIoWorkers)throw new NotSupportedException("Only one vCPU and no I/O/DMA workers supported");this.evictable=evictable??throw new ArgumentNullException(nameof(evictable));}
    void Owner(){if(Environment.CurrentManagedThreadId!=owner)throw new InvalidOperationException("VMM owner thread required");}
    public bool FailedPaused=>failed;
    public void BeginRun(){Owner();if(failed||running||leases!=0)throw new InvalidOperationException("Guest resume prohibited by CPU/pager barrier");running=true;}
    public void EndRun(){Owner();if(!running)throw new InvalidOperationException("No active vCPU run");running=false;}
    public IVmPause PauseCpuAndIo(){Owner();if(running||failed)throw new InvalidOperationException("Cannot page running/failed guest");leases++;return new Lease(this);}
    public bool IsEvictable(int page){Owner();if(running||leases==0||failed)throw new InvalidOperationException("Pager lease required");return evictable(page);}
    sealed class Lease:IVmPause
    {
        SingleVpQuiescence? q;public Lease(SingleVpQuiescence q){this.q=q;}
        public void KeepPausedOnFailure(){var x=q??throw new ObjectDisposedException(nameof(Lease));x.Owner();x.failed=true;}
        public void Dispose(){if(q==null)return;q.Owner();q.leases--;q=null;}
    }
}
