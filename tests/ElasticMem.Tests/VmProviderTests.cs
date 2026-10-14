using ElasticMem.Core.Backend.Vmm;
static class VmProviderTests
{
 sealed class Fake:IVmPageBacking,IVmCompressedStore,IVmQuiescence
 {
  public byte[] ram=Enumerable.Repeat((byte)31,4096).ToArray();byte[]? stored;public List<string> events=new();
  public bool mapped=true,committed=true,paused,failedPaused,pinned,putFails,decommitFails,mapFails,getFails,deleteFails,corruptGet;
  sealed class Lease:IVmPause{readonly Fake f;public Lease(Fake f){this.f=f;f.paused=true;f.events.Add("pause");}public void KeepPausedOnFailure(){f.failedPaused=true;}public void Dispose(){if(!f.failedPaused){f.paused=false;f.events.Add("resume");}}}
  public IVmPause PauseCpuAndIo()=>new Lease(this);public bool IsEvictable(int p)=>!pinned;
  void Guard(){if(!paused)throw new Exception("No CPU/I/O barrier");}
  public byte[] Read(int p){Guard();return (byte[])ram.Clone();}public void Write(int p,byte[] b){Guard();ram=(byte[])b.Clone();events.Add("write");}
  public void Unmap(int p){Guard();mapped=false;events.Add("unmap");}public void Map(int p){Guard();if(mapFails)throw new IOException();mapped=true;events.Add("map");}
  public void Decommit(int p){Guard();if(decommitFails)throw new IOException();committed=false;Array.Clear(ram);events.Add("decommit");}
  public void Commit(int p){Guard();committed=true;events.Add("commit");}
  public void Put(uint p,byte[] b){Guard();if(putFails)throw new IOException();stored=(byte[])b.Clone();events.Add("put");}
  public byte[] Get(uint p){Guard();if(getFails)throw new IOException();events.Add("get");var b=(byte[])stored!.Clone();if(corruptGet)b[0]^=1;return b;}
  public bool Delete(uint p){Guard();if(deleteFails)throw new IOException();stored=null;events.Add("delete");return true;}
 }
 public static int Run(){int checks=0,fails=0;void C(bool ok,string m){checks++;if(!ok){fails++;Console.WriteLine("FAIL VMM: "+m);}}
 VmPageProvider New(Fake f)=>new(1,f,f,f);
 var x=new Fake();var v=New(x);var e=v.Evict(0);C(e.ReleasedBackingCommitBytes==4096&&!x.committed&&!x.mapped,"source actually discarded in model");C(x.events.IndexOf("put")<x.events.IndexOf("unmap")&&x.events.IndexOf("unmap")<x.events.IndexOf("decommit"),"store before unmap before decommit");C(!x.paused&&v.State(0)==VmPageState.Stored,"stored unmapped state");v.RestoreBeforeGuestResume(0);C(x.ram.All(b=>b==31)&&x.mapped&&x.committed&&!x.paused,"full restore then resume");C(x.events.IndexOf("write")<x.events.IndexOf("map"),"write before map");
 x=new Fake{putFails=true};v=New(x);try{v.Evict(0);}catch{}C(x.committed&&x.mapped&&x.ram.All(b=>b==31),"quota/store failure preserves original");
 x=new Fake{pinned=true};v=New(x);try{v.Evict(0);}catch{}C(!x.events.Contains("put")&&x.committed,"DMA/pinned exclusion");
 x=new Fake{decommitFails=true};v=New(x);try{v.Evict(0);}catch{}C(x.mapped&&x.committed&&!x.failedPaused,"decommit failure remapped");
 x=new Fake{decommitFails=true,mapFails=true};v=New(x);try{v.Evict(0);}catch{}C(x.paused&&v.State(0)==VmPageState.FailedPaused,"rollback failure stays paused");
 x=new Fake();v=New(x);v.Evict(0);x.getFails=true;try{v.RestoreBeforeGuestResume(0);}catch{}C(x.paused&&!x.mapped&&v.State(0)==VmPageState.FailedPaused,"restore failure never resumes guest");
 x=new Fake();v=New(x);v.Evict(0);x.deleteFails=true;var r=v.RestoreBeforeGuestResume(0);C(r.CleanupPending&&x.mapped&&x.ram.All(b=>b==31),"stale compressed copy not authoritative");
 x=new Fake();v=New(x);v.Evict(0);x.corruptGet=true;try{v.RestoreBeforeGuestResume(0);}catch{}C(x.paused&&!x.mapped&&v.State(0)==VmPageState.FailedPaused,"corrupt stored page rejected before commit/map");
 bool rejected=false;try{New(new Fake()).Evict(1);}catch(ArgumentOutOfRangeException){rejected=true;}C(rejected,"bounds");
 Console.WriteLine($"VMM PROVIDER {checks} lifecycle checks; test double only, NOT Windows/WHP/SYS execution");return fails;
 }
}
