using System.Diagnostics;
using System.Text.Json;
using ElasticMem.Core.Research;
namespace ElasticMem.App.Backend;
internal static class WhpRunnerController
{
    public static string Reports=>Path.Combine(AppSettings.Dir,"whp-sys-reports");
    static async Task<string> ReadBounded(StreamReader reader,int limit)
    {
        var text=new System.Text.StringBuilder();char[] buffer=new char[4096];bool overflow=false;int n;
        while((n=await reader.ReadAsync(buffer))!=0){if(text.Length+n<=limit)text.Append(buffer,0,n);else overflow=true;}
        if(overflow)throw new InvalidDataException("Runner output limit exceeded");return text.ToString();
    }
    public static async Task<JsonElement> Run(bool inject)
    {
        string runner=Path.Combine(AppContext.BaseDirectory,"vmm-runner","ElasticMem.VmmRunner.exe");
        if(!File.Exists(runner))throw new FileNotFoundException("Нет vmm-runner в пакете. Никакая другая VM не будет запущена.");
        using var report=new ResearchRun(Reports,"whp-sys-synthetic",Updater.Current.ToString(),"custom_WHP_SYS",new{windows_guest=false,foreign_memory=false,inject_store_loss=inject});
        var start=new ProcessStartInfo(runner){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
        start.ArgumentList.Add("--research-disposable");start.ArgumentList.Add(inject?"inject-store-loss":"synthetic");start.ArgumentList.Add("32");
        using var process=new Process{StartInfo=start};
        try
        {
            if(!process.Start())throw new InvalidOperationException("Runner did not start");
            Task<string> stdout=ReadBounded(process.StandardOutput,1024*1024),stderr=ReadBounded(process.StandardError,64*1024);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try{await process.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){try{process.Kill(entireProcessTree:true);await process.WaitForExitAsync();}catch{}throw new TimeoutException("Only owned synthetic runner terminated; no valid paging result");}
            string text=await stdout;_=await stderr; // Do not export arbitrary native stderr.
            if(text.Length>1024*1024)throw new InvalidDataException("Oversized runner result");
            using var document=JsonDocument.Parse(text);var result=document.RootElement.Clone();
            if(result.GetProperty("backend").GetString()!="custom_WHP_SYS"||result.GetProperty("windows_guest_booted").GetBoolean()||result.GetProperty("physical_ram_saved_bytes").ValueKind!=JsonValueKind.Null)throw new InvalidDataException("Synthetic runner evidence scope mismatch");
            report.WriteJson("result.json",result);bool passed=process.ExitCode==0&&result.GetProperty("status").GetString()==(inject?"passed_injected_loss_kept_guest_paused":"passed_synthetic_guest_only");
            report.Seal(passed?"passed_synthetic_guest_only":"failed_no_valid_result","NOT Windows guest; no physical RAM saving proof; no Hyper-V VM fallback");return result;
        }
        catch(Exception ex)
        {
            var failure=new{schema="elasticmem.whp.sys.synthetic.v1",status="failed_no_valid_result",backend="custom_WHP_SYS",windows_guest_booted=false,error_class=ex.GetType().Name,physical_ram_saved_bytes=(long?)null,automatic_backend_fallback=false};
            report.WriteJson("result.json",failure);report.Seal("failed_no_valid_result","No native paging confirmation");return JsonSerializer.SerializeToElement(failure);
        }
    }
}
