using System.Runtime.InteropServices;
namespace ElasticMem.Vmm;

// Failure with the pipeline stage that failed. Only stage/type/HRESULT are reported; never messages or memory.
public sealed class VmmStageException:Exception
{
    public string Stage{get;}
    public VmmStageException(string stage,Exception inner):base("VMM stage failed: "+stage,inner){Stage=stage;HResult=inner.HResult;}
}

// Owned WHP partition with one vCPU running a tiny 16-bit real-mode guest. Not firmware, not Windows.
// The guest at GPA 0 executes: mov ax,[0]; inc ax; mov [2],ax; hlt  with DS = data page.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WhpSyntheticVm:IDisposable
{
    public const int Pages=64,FirstData=16,DataPages=16;
    public string Stage{get;private set;}="init";
    IntPtr partition;bool vp;bool disposed;
    public WhpPageBacking Backing{get;private set;}=null!;
    public int Exits{get;private set;}
    public static byte Pattern(int page)=>(byte)(0x31+page-FirstData);
    public static ushort Expected(int page)=>(ushort)(Pattern(page)*257+1);

    public T At<T>(string stage,Func<T> action){Stage=stage;try{return action();}catch(VmmStageException){throw;}catch(Exception ex){throw new VmmStageException(stage,ex);}}
    public void At(string stage,Action action)=>At(stage,()=>{action();return 0;});

    public static WhpSyntheticVm Create()
    {
        var vm=new WhpSyntheticVm();
        try
        {
            vm.At("partition_create",()=>{WhpNative.Check(WhpNative.WHvCreatePartition(out vm.partition));uint count=1;WhpNative.Check(WhpNative.WHvSetPartitionProperty(vm.partition,0x1fff,ref count,4));});
            vm.At("partition_setup",()=>WhpNative.Check(WhpNative.WHvSetupPartition(vm.partition)));
            vm.At("gpa_map",()=>{vm.Backing=new WhpPageBacking(vm.partition,0,Pages);for(int p=0;p<Pages;p++)vm.Backing.Map(p);});
            vm.At("guest_image",()=>
            {
                byte[] code=new byte[4096];new byte[]{0xA1,0,0,0x40,0xA3,2,0,0xF4}.CopyTo(code,0);vm.Backing.Write(0,code);
                for(int p=FirstData;p<FirstData+DataPages;p++){byte[] data=new byte[4096];Array.Fill(data,Pattern(p));vm.Backing.Write(p,data);}
            });
            vm.At("vp_create",()=>{WhpNative.Check(WhpNative.WHvCreateVirtualProcessor(vm.partition,0,0));vm.vp=true;});
            vm.At("vp_registers",()=>
            {
                // CS,DS,ES,SS,FS,GS,CR0,CR4,RIP,RFLAGS,RSP: flat real mode, interrupts off, code page 0.
                uint[] regs={0x13,0x15,0x12,0x14,0x16,0x17,0x1c,0x1f,0x10,0x11,0x04};
                var values=new[]{WhpNative.RegisterValue.Segment(0,0x9b),WhpNative.RegisterValue.Segment(0x1000,0x93),WhpNative.RegisterValue.Segment(0,0x93),WhpNative.RegisterValue.Segment(0,0x93),WhpNative.RegisterValue.Segment(0,0x93),WhpNative.RegisterValue.Segment(0,0x93),WhpNative.RegisterValue.Scalar(0x10),WhpNative.RegisterValue.Scalar(0),WhpNative.RegisterValue.Scalar(0),WhpNative.RegisterValue.Scalar(2),WhpNative.RegisterValue.Scalar(0xff0)};
                WhpNative.Check(WhpNative.WHvSetVirtualProcessorRegisters(vm.partition,0,regs,(uint)regs.Length,values));
            });
            return vm;
        }
        catch{vm.Dispose();throw;}
    }

    // Runs the guest against one data page. onUnmapped is called for GpaUnmapped memory exits and must
    // make the page resident (returns false for GPAs it does not own). RIP is not advanced: the faulting
    // instruction is retried. Returns the number of handled faults.
    public int Execute(int page,Func<ulong,bool>? onUnmapped,Action? beginRun=null,Action? endRun=null)
    {
        if(page<FirstData||page>=FirstData+DataPages)throw new ArgumentOutOfRangeException(nameof(page));
        At("vp_reset",()=>
        {
            // DS, RIP, RAX, InternalActivityState (clears HLT state from the previous run).
            uint[] reset={0x15,0x10,0x00,0x80000005};var vals=new[]{WhpNative.RegisterValue.Segment((ushort)(page*256),0x93),WhpNative.RegisterValue.Scalar(0),WhpNative.RegisterValue.Scalar(0),WhpNative.RegisterValue.Scalar(0)};
            WhpNative.Check(WhpNative.WHvSetVirtualProcessorRegisters(partition,0,reset,4,vals));
        });
        int faults=0;bool halted=false;
        for(int n=0;n<8&&!halted;n++)
        {
            var exit=At("vp_run",()=>
            {
                WhpNative.ExitContext e;int hr;int canceled=0;
                // Watchdog only cancels the run; memory is never freed from the timer thread. Callback joined before return.
                using(var timer=new System.Threading.Timer(_=>{Interlocked.Exchange(ref canceled,1);_=WhpNative.WHvCancelRunVirtualProcessor(partition,0,0);},null,10000,Timeout.Infinite))
                {
                    beginRun?.Invoke();try{hr=WhpNative.WHvRunVirtualProcessor(partition,0,out e,224);}finally{endRun?.Invoke();}
                    using var joined=new ManualResetEvent(false);timer.Dispose(joined);joined.WaitOne();
                }
                WhpNative.Check(hr);
                if(canceled!=0||e.Reason==WhpNative.ExitCanceled)throw new TimeoutException("WHP run watchdog expired; guest not resumed");
                return e;
            });
            Exits++;
            if(exit.Reason==WhpNative.ExitHalt){halted=true;break;}
            if(exit.Reason!=WhpNative.ExitMemoryAccess||!exit.GpaUnmapped||onUnmapped==null)
                throw new VmmStageException("vp_exit_unhandled",new InvalidOperationException("Unhandled WHP exit 0x"+exit.Reason.ToString("X")));
            if(!At("pager_restore",()=>onUnmapped(exit.Gpa)))throw new VmmStageException("pager_restore",new InvalidOperationException("Unowned/resident GPA fault; no zero-page substitution"));
            faults++;
        }
        if(!halted)throw new VmmStageException("guest_verify",new InvalidDataException("Guest did not halt"));
        At("guest_verify",()=>
        {
            var rax=new WhpNative.RegisterValue[1];WhpNative.Check(WhpNative.WHvGetVirtualProcessorRegisters(partition,0,new uint[]{0},1,rax));
            ushort expected=Expected(page);byte pattern=Pattern(page);
            if((ushort)rax[0].Value!=expected)throw new InvalidDataException("Guest read register mismatch");
            byte[] data=Backing.Read(page);
            try{if(BitConverter.ToUInt16(data,2)!=expected)throw new InvalidDataException("Guest write mismatch");for(int i=0;i<4096;i++)if(i!=2&&i!=3&&data[i]!=pattern)throw new InvalidDataException("Backing mismatch");}
            finally{Array.Clear(data);}
        });
        return faults;
    }

    // Teardown errors are collected instead of thrown from finally, so they never mask the original failure.
    public List<string> TeardownErrors{get;}=new();
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        void Try(string what,Action a){try{a();}catch(Exception ex){TeardownErrors.Add(what+":"+ex.GetType().Name+":0x"+ex.HResult.ToString("X8"));}}
        if(vp)Try("vp_delete",()=>WhpNative.Check(WhpNative.WHvDeleteVirtualProcessor(partition,0)));
        if(Backing!=null)Try("backing_dispose",Backing.Dispose);
        if(partition!=IntPtr.Zero)Try("partition_delete",()=>WhpNative.Check(WhpNative.WHvDeletePartition(partition)));
        partition=IntPtr.Zero;
    }
}
