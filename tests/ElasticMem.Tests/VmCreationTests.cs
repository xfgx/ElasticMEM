using System.Text.Json;
using System.Text.Json.Nodes;
using ElasticMem.Core.Backend;
static class VmCreationTests
{
 public static int Run()
 {
  int checks=0,fails=0;void C(bool ok,string m){checks++;if(!ok){fails++;Console.WriteLine("FAIL VM creation: "+m);}}
  var p=new VmCreationPlan("ElasticMem-New",@"C:\Images\Windows.vhdx",new string('a',64),"vhdx",CreationId:"48bcc13f-96e5-4363-ac52-6a03126d9095");p.Validate();C(true,"prepared VHDX accepted");
  void Reject(VmCreationPlan bad){bool failed=false;try{bad.Validate();}catch(ArgumentException){failed=true;}C(failed,"unsafe creation plan rejected");}
  Reject(p with{VmName="*"});Reject(p with{MediaPath=@"\\server\share\Windows.vhdx"});Reject(p with{MediaPath=@"\\?\C:\Windows.vhdx"});Reject(p with{MediaPath=@"C:\Windows.vhdx:secret"});Reject(p with{MediaPath=@"C:\..\Windows.vhdx"});Reject(p with{MediaPath=@"C:\.\Windows.vhdx"});Reject(p with{MediaPath="C:\\Images\\Windows.vhdx\r\n"});Reject(p with{MediaPath="https://example.test/windows.vhdx"});Reject(p with{MediaPath=@"C:\Windows.vhd"});Reject(p with{MediaSha256="unchecked"});Reject(p with{CreationId=Guid.Empty.ToString()});Reject(p with{CreationId=""});Reject(p with{SwitchId="Default Switch"});Reject(p with{SwitchId=Guid.Empty.ToString()});Reject(p with{CpuCount=0});Reject(p with{CpuCount=17});Reject(p with{DiskGiB=16});Reject(p with{MinimumMiB=8192});
  (p with{MediaKind="iso",MediaPath=@"D:\Images\Windows.ISO"}).Validate();C(true,"ISO plan accepted explicitly");
  Reject(p with{MediaKind="wim"});Reject(p with{MediaPath=@"C:\Windows.iso"});
  JsonObject Good()=>JsonSerializer.SerializeToNode(new{status="created_running_guest_unverified",vm_state="Running",creation_id=p.CreationId,vm_name=p.VmName,vm_id="7644ba64-dad6-45cc-b72e-20f074096253",generation=2,secure_boot="On",vtpm_enabled=true,private_copy_verified=true,source_modified=false,network_attached=false,media_kind=p.MediaKind,media_sha256=p.MediaSha256,custom_sys_pager=false})!.AsObject();
  JsonElement E(JsonObject o)=>JsonSerializer.SerializeToElement(o);
  var profile=VmCreationOutcome.Accept(p,E(Good()),"",true);C(profile.VmId!=""&&profile.RdpHost=="unconfigured.invalid"&&profile.AutoStartVm,"pair exact GUID without inventing guest/RDP readiness");
  void BadOutcome(string field,JsonNode? value){var o=Good();o[field]=value;bool rejected=false;try{VmCreationOutcome.Accept(p,E(o),"ElasticMem",false);}catch(InvalidDataException){rejected=true;}C(rejected,"failed/unsafe outcome never paired");}
  BadOutcome("status","failed");BadOutcome("vm_state","Off");BadOutcome("creation_id",Guid.NewGuid().ToString());BadOutcome("vm_id",Guid.Empty.ToString());BadOutcome("vm_name","other");BadOutcome("generation",1);BadOutcome("secure_boot","Off");BadOutcome("vtpm_enabled",false);BadOutcome("source_modified",true);BadOutcome("private_copy_verified",false);BadOutcome("network_attached",true);BadOutcome("media_sha256",new string('b',64));BadOutcome("custom_sys_pager",true);
  C(VmCredentialScope.FileName(profile.VmId)!=VmCredentialScope.FileName(Guid.NewGuid().ToString()),"VM credentials do not cross VM identity");C(VmCredentialScope.FileName(profile.VmId)==VmCredentialScope.FileName(profile.VmId.ToUpperInvariant()),"same GUID canonical scope");
  Console.WriteLine($"VM CREATION {checks} portable contract checks; NOT native Hyper-V creation");return fails;
 }
}
