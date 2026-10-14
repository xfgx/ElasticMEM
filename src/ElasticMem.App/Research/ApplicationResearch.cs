using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ElasticMem.Core.Research;
namespace ElasticMem.App.Research;
internal sealed record ApplicationTarget(int Pid,string Name,long StartTicks,long WorkingSetBytes);
internal static class ApplicationResearch
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder name,ref uint size);
    static void Confirm(IntPtr h,ApplicationTarget target)
    {
        if(!new[]{"chrome","msedge","Telegram"}.Contains(target.Name,StringComparer.OrdinalIgnoreCase))throw new NotSupportedException("Application outside allowlist");
        if(!Native.GetProcessTimes(h,out long creation,out long exit,out _,out _)||exit!=0||DateTime.FromFileTimeUtc(creation).Ticks!=target.StartTicks)throw new InvalidOperationException("Application lifetime changed");
        var image=new StringBuilder(32768);uint n=32768;
        if(!QueryFullProcessImageName(h,0,image,ref n)||!Path.GetFileNameWithoutExtension(image.ToString()).Equals(target.Name,StringComparison.OrdinalIgnoreCase))throw new NotSupportedException("Application image mismatch");
        using var self=Process.GetCurrentProcess();
        if(!Native.ProcessIdToSessionId(target.Pid,out int session)||session!=self.SessionId)throw new NotSupportedException("Only current-session applications supported");
        if(!Native.IsWow64Process2(h,out var pm,out var nm)||pm!=0||nm!=0x8664)throw new NotSupportedException("Only native x64 supported");
        if(!target.Name.Equals("Telegram",StringComparison.OrdinalIgnoreCase))AutoResearch.ConfirmRenderer(h,target.Pid,target.Name,target.StartTicks);
        if(!Native.GetProcessTimes(h,out long final,out long stopped,out _,out _)||final!=creation||stopped!=0)throw new InvalidOperationException("Application lifetime changed during confirmation");
    }
    internal static ApplicationTarget[] Discover()
    {
        var result=AutoResearch.Discover().Where(x=>x.Family is "chrome" or "msedge").Select(x=>new ApplicationTarget(x.Pid,x.Family,x.StartTicks,x.WorkingSet)).ToList();
        using var self=Process.GetCurrentProcess();
        foreach(var p in Process.GetProcessesByName("Telegram"))using(p)
        {
            IntPtr h=IntPtr.Zero;
            try{if(p.SessionId!=self.SessionId)continue;var t=new ApplicationTarget(p.Id,p.ProcessName,p.StartTime.ToUniversalTime().Ticks,p.WorkingSet64);h=Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION|Native.PROCESS_VM_READ,false,p.Id);if(h==IntPtr.Zero)continue;Confirm(h,t);result.Add(t);}catch{}finally{if(h!=IntPtr.Zero)Native.CloseHandle(h);}
        }
        return result.GroupBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).SelectMany(g=>g.OrderByDescending(x=>x.WorkingSetBytes).ThenBy(x=>x.Pid).Take(g.Key.Equals("Telegram",StringComparison.OrdinalIgnoreCase)?1:2)).ToArray();
    }
    internal static object Measure(ResearchOptions options,ResearchRun run,IProgress<string>? progress,CancellationToken ct)
    {
        if(options.AllowTrim||options.Pids.Length!=1||options.SnapshotMiB is <1 or >8||options.ExpectedStarts==null||!options.ExpectedStarts.TryGetValue(options.Pids[0],out long expected))throw new ArgumentException("Identity-bound one-process snapshot <=8MiB required; no trim");
        int pid=options.Pids[0];using var p=Process.GetProcessById(pid);var target=new ApplicationTarget(pid,p.ProcessName,expected,0);
        IntPtr h=Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION|Native.PROCESS_VM_READ,false,pid);if(h==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            ct.ThrowIfCancellationRequested();if(Native.MemoryStatus().ullAvailPhys<512UL<<20)throw new NotSupportedException("Application snapshot free RAM guard: need512MiB currently free");Confirm(h,target);
            if(!Native.TryGetMemInfo(h,out var before))throw new IOException("Application counters before snapshot unavailable");
            var snapshot=WindowsCapture.Capture(pid,options.SnapshotMiB,run,ct,expected);Confirm(h,target);
            if(!Native.TryGetMemInfo(h,out var after))throw new IOException("Application counters after snapshot unavailable");
            var codec=CodecBench.Run(new[]{new CodecInput(pid,expected,snapshot.Pages.Select(x=>x.Address).ToArray(),snapshot.Pages.Select(x=>x.Bytes).ToArray())},run.DirectoryPath,progress==null?null:progress.Report,ct,false);
            return new{schema="elasticmem.real_app_sample.v1",application=target.Name,pid,start_ticks=expected,captured_bytes=(long)snapshot.Pages.Length*4096,captured_pages=snapshot.Pages.Length,
                before_working_set_bytes=before.WorkingSetSize.ToUInt64(),after_capture_working_set_bytes=after.WorkingSetSize.ToUInt64(),before_private_commit_bytes=before.PrivateUsage.ToUInt64(),after_capture_private_commit_bytes=after.PrivateUsage.ToUInt64(),
                codec_comparison=codec,live_reclaim_performed=false,physical_ram_saved_bytes=(long?)null,application_speedup=(double?)null,
                limitations=new[]{"Read-only foreign process capture is perturbing and non-atomic; pre/post residency guards are not proof of zero page faults","Only bounded resident private exclusive pages, address-prefix biased sample, not whole application or Windows compressed store","Two rounds are repeated observations, not independent samples or an A/B performance experiment","Real application bytes verified through serialized cold-store decode locally; foreign process continues using original memory","No user-input automation, external process trimming, OS changes, injected code, remote PC access or live compressed backing"}};
        }
        finally{Native.CloseHandle(h);}
    }
    public static async Task<AutoOutcome> RunAsync(bool consent,IProgress<string>? progress,CancellationToken ct)
    {
        AutoResearch.EnsureNoOtherInstances();
        if(!consent)throw new ArgumentException("Explicit application memory-read consent required");
        string batch=Path.Combine(AppSettings.Dir,"research-batches",DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")+"-applications");string root=Path.Combine(batch,"runs");Directory.CreateDirectory(root);
        using var summary=new ResearchRun(root,"applications-summary",Updater.Current.ToString(),ResearchJobs.Commit,new{rounds=2,snapshot_mib=8,selection="up to2 Edge renderers,2 Chrome renderers,1 Telegram; same-session native x64; frozen identity; no replacement",memory_read_consent=consent});
        var rows=new List<object>();var text=new StringBuilder("Проверка реальных приложений на ЭТОМ ПК\r\nЭто тест данных/кодеков, НЕ режим экономии RAM и НЕ A/B ускорения.\r\n\r\n");string last=summary.DirectoryPath;bool failed=false;int verified=0;
        try
        {
            var test=await Task.Run(()=>ResearchJobs.Execute("selftest",new(Array.Empty<int>()),progress,ct,root));ResearchRun.Verify(test);
            using(var m=JsonDocument.Parse(File.ReadAllText(Path.Combine(test,"manifest.json"))))if(m.RootElement.GetProperty("status").GetString()!="selftests_passed")throw new InvalidOperationException("Selftest failed; application capture not started");
            var targets=await Task.Run(Discover,ct);
            foreach(string name in new[]{"msedge","chrome","Telegram"})if(!targets.Any(x=>x.Name.Equals(name,StringComparison.OrdinalIgnoreCase))){text.AppendLine(name+": нет доступной подтверждённой native-x64 цели (не запущена/нет доступа/другая роль). Не измерено.");rows.Add(new{application=name,status="unavailable_not_measured"});}
            for(int round=1;round<=2;round++)
            {
                foreach(var target in targets)
                {
                    ct.ThrowIfCancellationRequested();AutoResearch.EnsureNoOtherInstances();progress?.Report($"Приложения: {target.Name}, PID {target.Pid}, снимок {round}/2");
                    last=await Task.Run(()=>ResearchJobs.Execute("app-codecs",new(new[]{target.Pid},SnapshotMiB:8,ExpectedStarts:new Dictionary<int,long>{{target.Pid,target.StartTicks}}),progress,ct,root));ResearchRun.Verify(last);
                    using var m=JsonDocument.Parse(File.ReadAllText(Path.Combine(last,"manifest.json")));string status=m.RootElement.GetProperty("status").GetString()!;
                    rows.Add(new{application=target.Name,pid=target.Pid,start_ticks=target.StartTicks,round,status,folder=Path.GetFileName(last)});
                    if(status!="real_app_sample_codec_verified_not_live_reclaim"){failed=true;text.AppendLine($"{target.Name} PID {target.Pid}, снимок {round}: {status}; цель не заменена.");continue;}
                    verified++;using var d=JsonDocument.Parse(File.ReadAllText(Path.Combine(last,"result.json")));var result=d.RootElement;
                    text.AppendLine($"{target.Name} PID {target.Pid}, снимок {round}: {result.GetProperty("captured_bytes").GetInt64()/1048576.0:F2} МиБ реальных выбранных страниц; сериализованное восстановление проверено.");
                    foreach(var c in result.GetProperty("codec_comparison").GetProperty("codecs").EnumerateArray())if(c.GetProperty("status").GetString()=="serialized_roundtrip_checked")text.AppendLine(FormattableString.Invariant($"  {c.GetProperty("codec").GetString()}: store+reserve {c.GetProperty("total_with_reserve_bytes").GetInt64()/1048576.0:F2} MiB; warmed restore p50 {c.GetProperty("stored_payload_restore_p50_us_per_block").GetDouble():F2} us/block ({c.GetProperty("block_bytes").GetInt32()} B)."));
                }
                if(round==1&&targets.Length>0){progress?.Report("Продолжайте обычную работу с приложениями; второй снимок через15с.");await Task.Delay(15000,ct);}
            }
            text.AppendLine("\r\nИтог: проверена применимость кодеков к выбранным байтам реальных приложений. Экономия физической RAM и влияние на скорость НЕ измерены: live backing/перехват page faults отсутствует. Нельзя переносить проценты выборки на весь рабочий набор. Нули/неудачи не являются отрицательным результатом приложения.");
        }
        catch(OperationCanceledException ex){failed=true;summary.Log(ex.ToString());text.AppendLine("Отменено; частичные данные не полный результат.");}
        catch(Exception ex){failed=true;summary.Log(ex.ToString());text.AppendLine("Ошибка: "+ex.Message);}
        string statusFinal=failed?"failed_or_partial_application_checks":verified==0?"unavailable_no_application_measured":"real_application_samples_checked_not_optimizer";
        summary.WriteJson("result.json",new{schema="elasticmem.real_app_batch.v1",status=statusFinal,verified_samples=verified,rows,physical_ram_saved_bytes=(long?)null,live_reclaim_performed=false,application_speedup=(double?)null});
        summary.WriteJson("summary.json",new{status=statusFinal,verified_samples=verified});
        string report=text.ToString();File.WriteAllText(summary.FilePath("applications-report.txt"),report);summary.Seal(statusFinal,"See applications-report.txt; samples are not a live optimization.");ResearchRun.Verify(summary.DirectoryPath);
        string zip=Path.Combine(batch,"ElasticMem-logs-"+Path.GetFileName(batch)+".zip");ResearchRun.ExportLogs(root,zip,false);
        return new(root,last,zip,report+"\r\nZIP без дампов: "+zip,statusFinal);
    }
}
