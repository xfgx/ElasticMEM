using System.Text.Json;
using ElasticMem.DriverClient;
if(!OperatingSystem.IsWindows()){Console.Error.WriteLine("Windows 11 x64 signed KMDF driver and elevated client required.");return 2;}
if(args.Length==0 || args[0] is not("info" or "smoke" or "pager-smoke")){Console.Error.WriteLine("Usage: ElasticMem.DriverClient info | smoke [output.json] | pager-smoke [output.json]");return 2;}
try
{
    object result;
    if(args[0]=="info"){using var store=new KernelPageStore();result=store.Stats();}
    else result=args[0]=="pager-smoke"?PagerSmoke.Run():KernelSmoke.Run();
    string json=JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower});
    if(args.Length>1)File.WriteAllText(args[1],json);else Console.WriteLine(json);return 0;
}
catch(Exception ex)
{
    string json=JsonSerializer.Serialize(new{schema="elasticmem.kernel.smoke.v1",status="failed_no_valid_result",error_class=ex.GetType().Name,error=ex.Message,transparent_foreign_memory_supported=false});
    if(args.Length>1)File.WriteAllText(args[1],json);Console.Error.WriteLine(json);return 1;
}
