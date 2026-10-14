using ElasticMem.Core.Research;
namespace ElasticMem.App.Research;
internal static class ResearchCli
{
    public static bool TryRun(string[] args)
    {
        int at=Array.IndexOf(args,"--research");if(at<0)return false;
        string? Get(string name){int i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:null;}
        int Int(string name,int fallback)=>int.TryParse(Get(name),out int value)?value:fallback;
        string mode=at+1<args.Length?args[at+1]:"selftest";
        string transcript=Get("--out")??Path.Combine(AppSettings.Dir,"research-cli-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")+".txt");
        try
        {
            if(mode=="g0-worker")
            {Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(transcript))!);File.WriteAllText(transcript,System.Text.Json.JsonSerializer.Serialize(EpochCalibration.Worker(),ResearchRun.Json));return true;}
            if(mode=="controlled-worker")
            {Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(transcript))!);File.WriteAllText(transcript,System.Text.Json.JsonSerializer.Serialize(ControlledResearch.Worker(),ResearchRun.Json));return true;}
            if(mode=="auto")
            {
                var auto=AutoResearch.RunAsync(args.Contains("--allow-memory-read"),Int("--seconds",120),null,CancellationToken.None,allowTrim:args.Contains("--allow-working-set-trim"),extended:args.Contains("--extended"),scratch:Get("--scratch")??Path.GetTempPath()).GetAwaiter().GetResult();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(transcript))!);File.WriteAllText(transcript,auto.Report);
                Environment.ExitCode=BatchPolicy.Failure(auto.Status)||string.IsNullOrEmpty(auto.Archive)?1:0;return true;
            }
            if(mode=="export") {if(Get("--out")==null)throw new ArgumentException("--out ZIP required");ResearchRun.ExportLogs(ResearchJobs.Root,transcript,args.Contains("--include-sensitive"));Environment.ExitCode=0;return true;}
            if(mode=="verify"){ResearchRun.Verify(Get("--folder")??throw new ArgumentException("--folder required"));return true;}
            if(mode=="epoch-snapshot")throw new ArgumentException("Internal automatic-only identity-bound capture; use auto --allow-memory-read");
            if(mode=="g1"&&!args.Contains("--allow-memory-read"))throw new ArgumentException("G1 requires explicit --allow-memory-read; dumps may contain private data");
            if(mode=="g1"&&string.IsNullOrWhiteSpace(Get("--pids")))throw new ArgumentException("G1 requires explicit external --pids; self-capture is not a valid workload");
            var options=new ResearchOptions((Get("--pids")??Environment.ProcessId.ToString()).Split(',',StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).Distinct().ToArray(),Int("--seconds",30),Int("--limit",16),Int("--records",100000),Int("--iterations",100),Int("--pool",32),Int("--adapter",0),Get("--scratch")??Path.GetTempPath(),args.Contains("--negative"),Get("--trace")??"",Get("--metadata")??"",Int("--eviction-us",10),Int("--recovery-us",100),Int("--max-fault-us",100),SourceRun:Get("--source-folder")??Get("--g1-folder")??"",AllowTrim:args.Contains("--allow-working-set-trim"),Epochs:Int("--epochs",args.Contains("--extended")?600:12),Extended:args.Contains("--extended"),EpochPid:Int("--epoch-pid",0));
            var modes=mode=="suite"?new[]{"environment","selftest","g0-sample","g2","gpu-probe"}:new[]{mode};var lines=new List<string>();
            foreach(var key in modes)
            {
                string folder=ResearchJobs.Execute(key,options,null,CancellationToken.None);lines.Add(folder);lines.Add(File.ReadAllText(Path.Combine(folder,"report.md")));
                using var manifest=System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"manifest.json")));string status=manifest.RootElement.GetProperty("status").GetString()!;
                if(status.StartsWith("failed")||status.StartsWith("interrupted")||status.StartsWith("invalid")||status.StartsWith("cancelled"))Environment.ExitCode=1;else if(status.StartsWith("unsupported")&&Environment.ExitCode==0)Environment.ExitCode=3;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(transcript))!);File.WriteAllText(transcript,string.Join(Environment.NewLine,lines));
        }
        catch(Exception ex){Environment.ExitCode=1;Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(transcript))!);File.WriteAllText(transcript,ex.ToString());}
        return true;
    }
}
