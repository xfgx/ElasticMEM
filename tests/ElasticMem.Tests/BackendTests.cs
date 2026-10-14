using System.Text.Json;
using ElasticMem.Core.Backend;
static class BackendTests
{
 public static int Run(){int checks=0,fails=0;void C(bool ok,string message){checks++;if(!ok){fails++;Console.WriteLine("FAIL backend: "+message);}}
 var p=new BackendProfile("Backend VM","guest.example.test","ElasticMem");p.Validate();C(p.RdpFile("edge").Contains("||ElasticMem-Edge"),"published alias");C(p.RdpFile("chrome").Contains("authentication level:i:1"),"RDP trust enforced");C(!p.RdpFile("telegram").Contains("password")&&p.RdpFile("telegram").Contains("redirectclipboard:i:0"),"no credentials/clipboard delegation");
 void Reject(BackendProfile x){bool rejected=false;try{x.Validate();}catch(ArgumentException){rejected=true;}C(rejected,"invalid profile rejected");}
 Reject(p with{VmName="*"});Reject(p with{RdpHost="server\r\nredirectclipboard:i:1"});Reject(p with{VmName="x; Start-Process calc"});Reject(p with{MinimumMiB=8192,StartupMiB=4096});Reject(p with{VmId="other"});
 bool denied=false;try{p.RdpFile("cmd.exe");}catch(ArgumentException){denied=true;}C(denied,"arbitrary app rejected");
 JsonElement S(string id,string boot,string state,long assigned)=>JsonSerializer.SerializeToElement(new{vm_id=id,guest_boot_id=boot,vm_state=state,assigned_bytes=assigned});
 var a=S("vm1","boot1","Running",4096);var b=S("vm1","boot1","Running",2048);
 var d=JsonSerializer.SerializeToElement(BackendMeasurements.Compare(a,b));C(d.GetProperty("hyperv_assignment_reduction_bytes").GetInt64()==2048,"assignment delta");C(d.GetProperty("physical_ram_saved_bytes").ValueKind==JsonValueKind.Null,"no invented physical saving");
 denied=false;try{BackendMeasurements.Compare(a,S("vm2","boot1","Running",2048));}catch(InvalidOperationException){denied=true;}C(denied,"VM identity guard");
 denied=false;try{BackendMeasurements.Compare(a,S("vm1","boot2","Running",2048));}catch(InvalidOperationException){denied=true;}C(denied,"boot guard");
 denied=false;try{BackendMeasurements.Compare(a,S("vm1","boot1","Off",0));}catch(InvalidOperationException){denied=true;}C(denied,"off state not measured saving");
 Console.WriteLine($"BACKEND {checks} portable contract checks; NOT native Hyper-V/RDS execution");return fails;
 }
}
