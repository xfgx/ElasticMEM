using System.Text.Json;
using ElasticMem.Vmm;
var options=new JsonSerializerOptions{WriteIndented=true};
if(args.Length==1&&args[0]=="--probe"){Console.WriteLine(JsonSerializer.Serialize(WhpNative.Probe(),options));return 0;}
// Explicit consent, no installation, no elevation, no external guest images/disks or credentials.
if(args.Length<2||args[0]!="--research-disposable"||args[1] is not ("synthetic" or "inject-store-loss")){Console.Error.WriteLine("Use --probe OR --research-disposable synthetic|inject-store-loss [cycles 1..1024]. No Windows boot implemented.");return 2;}
try
{
    if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
    int cycles=args.Length==3?int.Parse(args[2]):32;if(args.Length>3)throw new ArgumentException();
    var result=WhpSyntheticPager.Run(cycles,injectGetFailure:args[1]=="inject-store-loss");Console.WriteLine(JsonSerializer.Serialize(result,options));return 0;
}
catch(Exception ex)
{
    // Never serialize arbitrary exception messages, guest registers or memory.
    Console.WriteLine(JsonSerializer.Serialize(new{schema="elasticmem.whp.sys.synthetic.v1",status="failed_no_valid_result",backend="custom_WHP_SYS",error_class=ex.GetType().Name,hresult=ex.HResult,windows_guest_booted=false,physical_ram_saved_bytes=(long?)null,automatic_backend_fallback=false},options));return 1;
}
