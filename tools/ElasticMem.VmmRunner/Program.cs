using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using ElasticMem.Core.Backend.Vmm;
using ElasticMem.DriverClient;
using ElasticMem.Vmm;

// Safety net: a NaN/Infinity metric must never turn a finished measurement into a serializer crash.
var options=new JsonSerializerOptions{WriteIndented=true,NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals};
void Print(object o)=>Console.WriteLine(JsonSerializer.Serialize(o,options));
const string Usage="Use: --probe | --selftest-user-store | --research-disposable synthetic|inject-store-loss [cycles 1..1024] [--store sys|user] | --research-disposable host-trim [rounds 1..32]. No Windows boot implemented.";

static object OsInfo()
{
    var v=Environment.OSVersion.Version;bool elevated=false;
    if(OperatingSystem.IsWindows()){using var id=WindowsIdentity.GetCurrent();elevated=new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);}
    return new{is_windows=OperatingSystem.IsWindows(),version=v.ToString(),build=v.Build,is_windows11_build=OperatingSystem.IsWindows()&&v.Build>=22000,process_arch=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),elevated};
}
static object SysProbe()
{
    if(!OperatingSystem.IsWindows())return new{status="requires_windows"};
    try
    {
        using var store=new KernelPageStore();
        try{return new{status="ok",capabilities=store.Capabilities(),stats=store.Stats()};}
        catch(InvalidDataException){return new{status="protocol_unsupported_old_or_foreign_driver"};}
        catch(Win32Exception ex){return new{status="protocol_unsupported_old_or_foreign_driver",win32_error=ex.NativeErrorCode};}
    }
    catch(Win32Exception ex)
    {
        string status=ex.NativeErrorCode switch{2=>"not_installed_or_not_started",3=>"not_installed_or_not_started",5=>"access_denied_requires_elevated_admin",1450=>"insufficient_resources",_=>"open_failed"};
        return new{status,win32_error=ex.NativeErrorCode};
    }
    catch(InvalidDataException){return new{status="protocol_unsupported_old_or_foreign_driver"};}
}
static object Failure(string mode,string backend,Exception ex)
{
    // Never serialize arbitrary exception messages, guest registers or memory: stage + types + codes only.
    var stage=ex as VmmStageException;var root=stage?.InnerException??ex;
    string? whpStatus=stage?.Stage=="whp_probe"?root.Message:null; // fixed status token produced by WhpNative.Probe
    return new{schema="elasticmem.whp.runner.failure.v2",status="failed_no_valid_result",mode,backend,failed_stage=stage?.Stage??"unknown",error_class=root.GetType().Name,
        hresult="0x"+root.HResult.ToString("X8"),win32_error=root is Win32Exception w?w.NativeErrorCode:(int?)null,whp_status=whpStatus,
        os=OsInfo(),windows_guest_booted=false,physical_ram_saved_bytes=(long?)null,automatic_backend_fallback=false};
}

if(args.Length==1&&args[0]=="--probe")
{
    var whp=WhpNative.Probe();var sys=SysProbe();
    Print(new{schema="elasticmem.vmm.probe.v2",os=OsInfo(),whp,sys,
        user_store_path_possible=whp.Available,sys_store_path_possible=whp.Available&&sys.GetType().GetProperty("status")?.GetValue(sys) as string=="ok",
        note="Read-only probe. Nothing installed, enabled or changed. WHP requires the Windows hypervisor (feature HypervisorPlatform)."});
    return 0;
}
if(args.Length==1&&args[0]=="--selftest-user-store")
{
    try{if(!OperatingSystem.IsWindows())throw new VmmStageException("platform",new PlatformNotSupportedException("requires_windows_x64"));var r=UserStoreSelfTest.Run();try{Print(r);}catch(Exception ex){throw new VmmStageException("serialize_result",ex);}return 0;}
    catch(Exception ex){Print(Failure("selftest-user-store","user_ntdll_xpress",ex));return 1;}
}
if(args.Length<2||args[0]!="--research-disposable"||args[1] is not ("synthetic" or "inject-store-loss" or "host-trim")){Console.Error.WriteLine(Usage);return 2;}
string mode=args[1];var rest=args.Skip(2).ToList();var store=PagerStoreKind.Sys;
int si=rest.IndexOf("--store");
if(si>=0){if(si+1>=rest.Count||rest[si+1] is not ("sys" or "user")||mode=="host-trim"){Console.Error.WriteLine(Usage);return 2;}store=rest[si+1]=="user"?PagerStoreKind.User:PagerStoreKind.Sys;rest.RemoveRange(si,2);}
if(rest.Count>1||rest.Count==1&&!int.TryParse(rest[0],out _)){Console.Error.WriteLine(Usage);return 2;}
int count=rest.Count==1?int.Parse(rest[0]):mode=="host-trim"?3:32;
string backend=mode=="host-trim"?"custom_WHP_host_trim_probe":OperatingSystem.IsWindows()?WhpSyntheticPager.BackendName(store):store==PagerStoreKind.Sys?"custom_WHP_SYS":"custom_WHP_user_store";
try
{
    if(!OperatingSystem.IsWindows())throw new VmmStageException("platform",new PlatformNotSupportedException("requires_windows_x64"));
    object result=mode=="host-trim"?WhpHostTrimProbe.Run(count):WhpSyntheticPager.Run(count,store,injectGetFailure:mode=="inject-store-loss");
    try{Print(result);}catch(Exception ex){throw new VmmStageException("serialize_result",ex);}return 0;
}
catch(Exception ex){Print(Failure(mode,backend,ex));return 1;}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static class UserStoreSelfTest
{
    public static object Run()
    {
        using var codec=new NtXpressCodec();using var store=new UserPageStore(codec,1024,64L*1024*1024);
        var rng=new Random(12345);var kinds=new (string name,Func<int,byte[]> make,bool compressible)[]
        {
            ("zeros",_=>new byte[4096],true),
            ("constant",i=>{var b=new byte[4096];Array.Fill(b,(byte)(i|1));return b;},true),
            ("small_ints",i=>{var b=new byte[4096];for(int k=0;k<1024;k++)BitConverter.TryWriteBytes(b.AsSpan(k*4),rng.Next(256));return b;},true),
            ("text",i=>{var b=new byte[4096];var s=System.Text.Encoding.ASCII.GetBytes("ElasticMem page "+i+" lorem ipsum dolor sit amet ");for(int k=0;k<4096;k++)b[k]=s[k%s.Length];return b;},true),
            ("random",_=>{var b=new byte[4096];rng.NextBytes(b);return b;},false),
        };
        var rows=new List<object>();int failures=0;uint key=0;
        foreach(var (name,make,compressible) in kinds)
        {
            int stored=0,rejected=0,mismatch=0;long payload=0;var put=new List<double>();var get=new List<double>();
            for(int i=0;i<128;i++,key++)
            {
                byte[] page=make(i);var t=Stopwatch.StartNew();
                VmStoreReceipt receipt;
                try{receipt=store.Put(key,page);t.Stop();put.Add(t.Elapsed.TotalMicroseconds);}
                catch(PageNotBeneficialException){rejected++;continue;}
                receipt.Validate();stored++;payload+=receipt.PayloadBytes;
                t.Restart();byte[] back=store.Get(key,receipt.Generation);t.Stop();get.Add(t.Elapsed.TotalMicroseconds);
                if(!back.AsSpan().SequenceEqual(page))mismatch++;
                if(!store.Delete(key,receipt.Generation))mismatch++;
            }
            bool ok=mismatch==0&&(compressible?stored==128:rejected==128);if(!ok)failures++;
            static double? Median(List<double> x){if(x.Count==0)return null;x.Sort();return x[x.Count/2];}
            rows.Add(new{kind=name,pages=128,stored,rejected_not_beneficial=rejected,mismatches=mismatch,mean_payload_bytes=stored==0?0:payload/stored,put_us_median=Median(put),get_us_median=Median(get),ok});
        }
        var stats=store.Stats();
        if(stats.Pages!=0||stats.LedgerBytes!=0)failures++;
        if(failures!=0)throw new VmmStageException("user_store_selftest",new InvalidDataException("User store self-test failed"));
        return new{schema="elasticmem.user-store.selftest.v1",status="passed",codec=codec.Name,rows,final_ledger=stats,note="Native ntdll XPRESS roundtrip of synthetic pages. Not a guest/VMM test."};
    }
}
