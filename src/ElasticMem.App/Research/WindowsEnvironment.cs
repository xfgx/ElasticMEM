using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using ElasticMem.Core.Research;
namespace ElasticMem.App.Research;
internal static class WindowsEnvironment
{
    public static object Read(ResearchRun run)
    {
        var memory=Native.MemoryStatus();object? mma=null;string? error=null;
        try
        {
            var start=new ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-Command");start.ArgumentList.Add("Get-MMAgent | ConvertTo-Json -Compress");
            using var p=Process.Start(start)!;var output=p.StandardOutput.ReadToEndAsync();var errors=p.StandardError.ReadToEndAsync();
            if(!p.WaitForExit(15000)){p.Kill(true);throw new TimeoutException("Read-only Get-MMAgent timed out");}
            string raw=output.GetAwaiter().GetResult(),stderr=errors.GetAwaiter().GetResult();run.WriteJson("mma-query.json",new{exit_code=p.ExitCode,stdout=raw,stderr});
            if(p.ExitCode==0)mma=JsonSerializer.Deserialize<JsonElement>(raw);else error="Get-MMAgent unavailable; see mma-query.json";
        }
        catch(Exception ex){error=ex.Message;}
        long? compressionWs=null;int observed=0;
        foreach(var candidate in Process.GetProcessesByName("MemCompression"))using(candidate)try{compressionWs=(compressionWs??0)+candidate.WorkingSet64;observed++;}catch{}
        return new{schema="elasticmem.windows.environment.v2",os=Environment.OSVersion.ToString(),x64_process=Environment.Is64BitProcess,x64_os=Environment.Is64BitOperatingSystem,cpu_threads=Environment.ProcessorCount,total_physical_bytes=memory.ullTotalPhys,available_physical_bytes=memory.ullAvailPhys,administrator=new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),mma_agent=mma,mma_error=error,
            compression_store=new{visible_process_count=observed,process_working_set_bytes=compressionWs,compressed_payload_bytes=(long?)null,logical_uncompressed_bytes=(long?)null,actual_ratio=(double?)null,measurement_status="process_WS_only_if_visible_not_compressed_payload",xpress_is_build_verified=false},
            limitations=new[]{"Read-only capabilities, not measured memory saving","PageCombining status does not quantify shared physical pages or incremental G1 benefit","No enabling PageCombining, privilege elevation or NtSetSystemInformation call performed"}};
    }
}
