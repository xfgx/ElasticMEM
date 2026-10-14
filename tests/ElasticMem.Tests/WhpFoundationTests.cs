using System.Runtime.InteropServices;
using ElasticMem.Core.Backend.Vmm;
using ElasticMem.Vmm;
static class WhpFoundationTests
{
    public static int Run()
    {
        int checks=0,fails=0;void C(bool ok,string name){checks++;if(!ok){fails++;Console.WriteLine("FAIL WHP foundation: "+name);}}
        bool Reject(Action f){try{f();return false;}catch(InvalidOperationException){return true;}catch(NotSupportedException){return true;}}
        C(Marshal.SizeOf<WhpNative.RegisterValue>()==16,"register ABI");C(Marshal.SizeOf<WhpNative.ExitContext>()==224,"x64 exit ABI");
        C(Marshal.OffsetOf<WhpNative.ExitContext>(nameof(WhpNative.ExitContext.Gpa)).ToInt32()==72,"GPA offset");
        C(Marshal.OffsetOf<WhpNative.ExitContext>(nameof(WhpNative.ExitContext.AccessInfo)).ToInt32()==68,"access info offset");
        var segment=WhpNative.RegisterValue.Segment(0x1000,0x93);C(segment.Value==0x10000&&segment.Limit==0xffff&&segment.Selector==0x1000&&segment.Attributes==0x93,"real-mode data segment");
        C(new WhpNative.ExitContext{AccessInfo=4}.GpaUnmapped&&!new WhpNative.ExitContext{AccessInfo=2}.GpaUnmapped,"unmapped vs permission fault");
        var q=new SingleVpQuiescence(1,false,p=>p!=0);C(Reject(()=>new SingleVpQuiescence(2,false,_=>true)),"reject multiVP unsupported");C(Reject(()=>new SingleVpQuiescence(1,true,_=>true)),"reject DMA workers unsupported");
        q.BeginRun();C(Reject(()=>q.PauseCpuAndIo()),"cannot evict active vCPU");C(Reject(q.BeginRun),"recursive run prohibited");q.EndRun();
        using(var pause=q.PauseCpuAndIo()){C(!q.IsEvictable(0)&&q.IsEvictable(1),"pinned page");C(Reject(q.BeginRun),"run excluded during lease");using var nested=q.PauseCpuAndIo();C(Reject(q.BeginRun),"nested lease exclusion");}
        q.BeginRun();q.EndRun();C(!q.FailedPaused,"successful lease releases barrier");
        bool threadRejected=Task.Run(()=>Reject(q.BeginRun)).GetAwaiter().GetResult();C(threadRejected,"cross-thread run denied");
        using(var lease=q.PauseCpuAndIo())lease.KeepPausedOnFailure();C(q.FailedPaused&&Reject(q.BeginRun),"failure permanently prohibits resume");C(Reject(()=>q.PauseCpuAndIo()),"failed page cannot recursively retry");
        var policy=new ClockPagePolicy(4);for(int i=0;i<4;i++)policy.ObserveAccess(i);C(policy.Choose(p=>p!=0)==1,"second chance excludes pinned");C(policy.Choose(p=>p!=0)==2,"clock advances");C(policy.Choose(_=>false)==null,"bounded no eligible scan");
        C(policy.Choose(p=>p==3)==3,"resident eligibility");bool bounds=false;try{policy.ObserveAccess(4);}catch(ArgumentOutOfRangeException){bounds=true;}C(bounds,"reference bound");
        if(!OperatingSystem.IsWindows())C(!WhpNative.Probe().Available,"non-Windows no fallback");
        Console.WriteLine($"WHP FOUNDATION {checks} checks; ABI/model only, NOT WHP native execution/Windows boot");return fails;
    }
}
