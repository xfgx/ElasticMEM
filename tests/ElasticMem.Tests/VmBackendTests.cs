using System.Text.Json;
using ElasticMem.Core.Backend;
static class VmBackendTests
{
 public static int Run()
 {
  int checks=0,fails=0;void C(bool ok,string m){checks++;if(!ok){fails++;Console.WriteLine("FAIL VM backend: "+m);}}
  var sys=new BackendProfile("Our SYS project","unconfigured.invalid","ElasticMem",BackendKind:VmBackendKind.Sys);
  var hv=new BackendProfile("Existing VM","guest.example.test","ElasticMem");sys.Validate();hv.Validate();
  C((int)VmBackendKind.Sys==1&&(int)VmBackendKind.HyperV==2,"stable requested type numbers");
  C(JsonSerializer.Deserialize<BackendProfile>("{\"VmName\":\"Legacy\",\"RdpHost\":\"guest.example.test\",\"CollectionName\":\"ElasticMem\"}")!.BackendKind==VmBackendKind.HyperV,"legacy profile stays Hyper-V, never mislabeled SYS");
  bool denied=false;try{VmBackendPolicy.RequireHyperV(VmBackendKind.Sys);}catch(NotSupportedException){denied=true;}C(denied,"SYS route refuses Hyper-V backend");
  VmBackendPolicy.RequireHyperV(VmBackendKind.HyperV);C(true,"explicit Hyper-V accepted");
  denied=false;try{(sys with{BackendKind=(VmBackendKind)0}).Validate();}catch(ArgumentException){denied=true;}C(denied,"unknown/zero type rejected");
  denied=false;try{sys.RdpFile("edge");}catch(NotSupportedException){denied=true;}C(denied,"SYS does not launch Hyper-V RemoteApp");
  denied=false;try{(sys with{VmId=Guid.NewGuid().ToString()}).Validate();}catch(ArgumentException){denied=true;}C(denied,"no fabricated SYS VM runtime ID");
  denied=false;try{(sys with{AutoStartVm=true}).Validate();}catch(ArgumentException){denied=true;}C(denied,"no SYS VM autostart without VMM");
  var c=VmProfileCatalog.Empty.Add(sys);string sysId=c.ActiveId;c=c.Add(hv);string hvId=c.ActiveId;C(c.Entries.Count==2&&c.Active!.BackendKind==VmBackendKind.HyperV,"two independent per-VM types");
  c=c.Select(sysId);C(c.Active!.BackendKind==VmBackendKind.Sys,"select SYS independently");
  var serialized=JsonSerializer.Serialize(c);var round=JsonSerializer.Deserialize<VmProfileCatalog>(serialized)!;round.Validate();C(round.ActiveId==sysId&&round.Entries.Single(e=>e.Id==hvId).Profile.BackendKind==VmBackendKind.HyperV,"persist catalog and active identity");
  denied=false;try{c.Update(hv);}catch(InvalidOperationException){denied=true;}C(denied,"cannot silently convert an existing VM backend");
  c=c.Update(sys with{VmName="Renamed SYS"});C(c.Active!.VmName=="Renamed SYS"&&c.ActiveId==sysId,"edit preserves backend and profile identity");
  denied=false;try{c.Select(Guid.NewGuid().ToString());}catch(InvalidDataException){denied=true;}C(denied,"unknown profile selection rejected");
  denied=false;try{new VmProfileCatalog(sysId,new[]{new VmProfileEntry(sysId,sys),new VmProfileEntry(sysId,hv)}).Validate();}catch(InvalidDataException){denied=true;}C(denied,"duplicate profile identity rejected");
  denied=false;try{new VmProfileCatalog(Guid.NewGuid().ToString(),c.Entries).Validate();}catch(InvalidDataException){denied=true;}C(denied,"orphan active profile rejected");
  denied=false;try{new VmProfileCatalog(sysId,Enumerable.Range(0,33).Select(_=>new VmProfileEntry(Guid.NewGuid().ToString(),sys)).ToArray()).Validate();}catch(InvalidDataException){denied=true;}C(denied,"catalog bounded");
  denied=false;try{new VmCreationPlan("SYS",@"C:\Windows.vhdx",new string('a',64),"vhdx",CreationId:Guid.NewGuid().ToString(),BackendKind:VmBackendKind.Sys).Validate();}catch(NotSupportedException){denied=true;}C(denied,"Hyper-V creation plan cannot be used for SYS");
  var tuned=sys with{SysStoreCapacity=65536,SysStoreQuota=8UL*1024*1024};tuned.Validate();C(JsonSerializer.Deserialize<BackendProfile>(JsonSerializer.Serialize(tuned))!.SysStoreCapacity==65536,"per-profile SYS limits persisted");
  denied=false;try{(sys with{SysStoreCapacity=1048576,SysStoreQuota=1024*1024}).Validate();}catch(ArgumentException){denied=true;}C(denied,"metadata cannot exceed SYS quota");
  Console.WriteLine($"VM BACKENDS {checks} portable type/catalog guards; NOT native SYS/Hyper-V execution");return fails;
 }
}
