using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Globalization;
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
            var snapshot=WindowsCapture.Capture(pid,options.SnapshotMiB,run,ct,expected);
            try
            {
            Confirm(h,target);
            if(!Native.TryGetMemInfo(h,out var after))throw new IOException("Application counters after snapshot unavailable");
            var codec=CodecBench.Run(new[]{new CodecInput(pid,expected,snapshot.Pages.Select(x=>x.Address).ToArray(),snapshot.Pages.Select(x=>x.Bytes).ToArray())},run.DirectoryPath,progress==null?null:progress.Report,ct,false);
            object? kernel=options.KernelSamples?KernelApplicationSamples.Run(snapshot,run,progress,ct):null;
            return new{schema="elasticmem.real_app_sample.v2",application=target.Name,pid,start_ticks=expected,captured_bytes=(long)snapshot.Pages.Length*4096,captured_pages=snapshot.Pages.Length,
                before_working_set_bytes=before.WorkingSetSize.ToUInt64(),after_capture_working_set_bytes=after.WorkingSetSize.ToUInt64(),before_private_commit_bytes=before.PrivateUsage.ToUInt64(),after_capture_private_commit_bytes=after.PrivateUsage.ToUInt64(),
                codec_comparison=codec,kernel_comparison=kernel,live_reclaim_performed=false,physical_ram_saved_bytes=(long?)null,application_speedup=(double?)null,
                limitations=new[]{"Read-only foreign process capture is perturbing and non-atomic; pre/post residency guards are not proof of zero page faults","Only bounded resident private exclusive pages, address-prefix biased sample, not whole application or Windows compressed store","Two rounds are repeated observations, not independent samples or an A/B performance experiment","Real application bytes verified through serialized cold-store decode locally; foreign process continues using original memory","No user-input automation, external process trimming, OS changes, injected code, remote PC access or live compressed backing"}};
            }
            finally{foreach(var page in snapshot.Pages)Array.Clear(page.Bytes);}
        }
        finally{Native.CloseHandle(h);}
    }
    public static async Task<AutoOutcome> RunAsync(bool consent,IProgress<string>? progress,CancellationToken ct,bool kernelSamples=false,string application="all",bool researchVm=false)
    {
        if(!new[]{"all","msedge","chrome","Telegram"}.Contains(application,StringComparer.OrdinalIgnoreCase))throw new ArgumentException("Application must be all, msedge, chrome or Telegram");
        if(kernelSamples&&!researchVm)throw new ArgumentException("Kernel sample copies require explicit research VM acknowledgement");
        AutoResearch.EnsureNoOtherInstances();
        if(!consent)throw new ArgumentException("Explicit application memory-read consent required");
        string batch=Path.Combine(AppSettings.Dir,"research-batches",DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")+"-applications");string root=Path.Combine(batch,"runs");Directory.CreateDirectory(root);
        using var summary=new ResearchRun(root,"applications-summary",Updater.Current.ToString(),ResearchJobs.Commit,new{rounds=2,snapshot_mib=8,selection="up to2 Edge renderers,2 Chrome renderers,1 Telegram; same-session native x64; frozen identity; no replacement",memory_read_consent=consent,kernel_sample_copies=kernelSamples,research_vm_acknowledged=researchVm,application_filter=application});
        var rows=new List<object>();var text=new StringBuilder("Проверка реальных приложений на ЭТОМ ПК\r\nЭто тест данных/кодеков, НЕ режим экономии RAM и НЕ A/B ускорения.\r\n\r\n");string last=summary.DirectoryPath;bool failed=false;int verified=0;
        var metrics=new List<string>{"application,pid,round,backend,codec,block_bytes,pages,input_bytes,stored_bytes,stored_bytes_kind,ratio_input_to_stored,sample_representation_reduction_percent,put_or_encode_p50_us,put_or_encode_p95_us,put_or_encode_p99_us,restore_p50_us,restore_p95_us,restore_p99_us,full_bytes_verified,physical_ram_saved_bytes"};
        var requested=application.Equals("all",StringComparison.OrdinalIgnoreCase)?new[]{"msedge","chrome","Telegram"}:new[]{application};
        int unavailable=0;bool discoveryCompleted=false;
        text.AppendLine(kernelSamples?"VM: одни и те же копии страниц проверяются в user-mode кодеках и kernel RAW/XPRESS. Оригиналы приложения НЕ освобождаются.":"User-mode codec test: драйвер не нужен и не используется.");
        try
        {
            var test=await Task.Run(()=>ResearchJobs.Execute("selftest",new(Array.Empty<int>()),progress,ct,root));ResearchRun.Verify(test);
            using(var m=JsonDocument.Parse(File.ReadAllText(Path.Combine(test,"manifest.json"))))if(m.RootElement.GetProperty("status").GetString()!="selftests_passed")throw new InvalidOperationException("Selftest failed; application capture not started");
            var environment=await Task.Run(()=>ResearchJobs.Execute("environment",new(Array.Empty<int>()),progress,ct,root));ResearchRun.Verify(environment);
            if(kernelSamples)
            {
                var control=await Task.Run(()=>ResearchJobs.Execute("kernel-random-control",new(Array.Empty<int>()),progress,ct,root));ResearchRun.Verify(control);
                using var controlManifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(control,"manifest.json")));
                if(controlManifest.RootElement.GetProperty("status").GetString()!="synthetic_kernel_control_verified_not_application")throw new InvalidOperationException("Kernel random control failed; samples not started. Driver must already be trusted and installed.");
                text.AppendLine("Kernel: детерминированный random negative control прошёл RAW/XPRESS; это не результат приложения.");
            }
            var targets=(await Task.Run(Discover,ct)).Where(x=>requested.Contains(x.Name,StringComparer.OrdinalIgnoreCase)).ToArray();
            discoveryCompleted=true;
            foreach(string name in requested)if(!targets.Any(x=>x.Name.Equals(name,StringComparison.OrdinalIgnoreCase))){unavailable++;text.AppendLine(name+": нет доступной подтверждённой native-x64 цели (не запущена/нет доступа/другая роль). Не измерено.");rows.Add(new{application=name,status="unavailable_not_measured"});}
            for(int round=1;round<=2;round++)
            {
                foreach(var target in targets)
                {
                    ct.ThrowIfCancellationRequested();AutoResearch.EnsureNoOtherInstances();progress?.Report($"Приложения: {target.Name}, PID {target.Pid}, снимок {round}/2");
                    last=await Task.Run(()=>ResearchJobs.Execute("app-codecs",new(new[]{target.Pid},SnapshotMiB:8,ExpectedStarts:new Dictionary<int,long>{{target.Pid,target.StartTicks}},KernelSamples:kernelSamples),progress,ct,root));ResearchRun.Verify(last);
                    using var m=JsonDocument.Parse(File.ReadAllText(Path.Combine(last,"manifest.json")));string status=m.RootElement.GetProperty("status").GetString()!;
                    rows.Add(new{application=target.Name,pid=target.Pid,start_ticks=target.StartTicks,round,status,folder=Path.GetFileName(last)});
                    if(status!="real_app_sample_codec_verified_not_live_reclaim"){failed=true;text.AppendLine($"{target.Name} PID {target.Pid}, снимок {round}: {status}; цель не заменена.");continue;}
                    verified++;using var d=JsonDocument.Parse(File.ReadAllText(Path.Combine(last,"result.json")));var result=d.RootElement;
                    text.AppendLine($"{target.Name} PID {target.Pid}, снимок {round}: {result.GetProperty("captured_bytes").GetInt64()/1048576.0:F2} МиБ реальных выбранных страниц; сериализованное восстановление проверено.");
                    foreach(var c in result.GetProperty("codec_comparison").GetProperty("codecs").EnumerateArray())if(c.GetProperty("status").GetString()=="serialized_roundtrip_checked")text.AppendLine(FormattableString.Invariant($"  {c.GetProperty("codec").GetString()}: store+reserve {c.GetProperty("total_with_reserve_bytes").GetInt64()/1048576.0:F2} MiB; warmed restore p50 {c.GetProperty("stored_payload_restore_p50_us_per_block").GetDouble():F2} us/block ({c.GetProperty("block_bytes").GetInt32()} B)."));
                    AddMetrics(result,target.Name,target.Pid,round,metrics,text);

                }
                if(round==1&&targets.Length>0){progress?.Report("Продолжайте обычную работу с приложениями; второй снимок через15с.");await Task.Delay(15000,ct);}
            }
            text.AppendLine("\r\nИтог: проверена применимость кодеков к выбранным байтам реальных приложений. Экономия физической RAM и влияние на скорость НЕ измерены: live backing/перехват page faults отсутствует. Нельзя переносить проценты выборки на весь рабочий набор. Нули/неудачи не являются отрицательным результатом приложения.");
        }
        catch(OperationCanceledException ex){failed=true;summary.Log(ex.ToString());text.AppendLine("Отменено; частичные данные не полный результат.");}
        catch(Exception ex){failed=true;summary.Log(ex.ToString());text.AppendLine("Ошибка: "+ex.Message);}
        string statusFinal=failed?"failed_or_partial_application_checks":verified==0?"unavailable_no_application_measured":"real_application_samples_checked_not_optimizer";
        summary.WriteJson("result.json",new{schema="elasticmem.real_app_batch.v2",status=statusFinal,verified_samples=verified,application_filter=application,unavailable_families=discoveryCompleted?(int?)unavailable:null,coverage=!discoveryCompleted?"not_discovered":unavailable==0?"all_requested_families_available":"partial_requested_families_available",kernel_sample_copies=kernelSamples,rows,physical_ram_saved_bytes=(long?)null,live_reclaim_performed=false,application_speedup=(double?)null});
        summary.WriteJson("summary.json",new{status=statusFinal,verified_samples=verified});
        File.WriteAllLines(summary.FilePath("application-compression-summary.csv"),metrics);
        if(!discoveryCompleted)text.AppendLine("Выбор приложений не завершён: доступность семейств не измерена.");
        else text.AppendLine($"Доступность: {requested.Length-unavailable}/{requested.Length} запрошенных семейств; успешных снимков: {verified}. Недоступные цели не заменялись синтетическими данными.");
        string report=text.ToString();File.WriteAllText(summary.FilePath("applications-report.txt"),report);summary.Seal(statusFinal,"See applications-report.txt; samples are not a live optimization.");ResearchRun.Verify(summary.DirectoryPath);
        string zip=Path.Combine(batch,"ElasticMem-logs-"+Path.GetFileName(batch)+".zip");ResearchRun.ExportLogs(root,zip,false);
        return new(root,last,zip,report+"\r\nZIP без дампов: "+zip,statusFinal);
    }
    static void AddMetrics(JsonElement result,string application,int pid,int round,List<string> csv,StringBuilder text)
    {
        static string F(object? x)=>x is IFormattable f?f.ToString(null,CultureInfo.InvariantCulture):x?.ToString()??"";
        void Row(string backend,string codec,int blockBytes,int pages,long input,long stored,string kind,double encode50,double encode95,double? encode99,double restore50,double restore95,double? restore99)
        {
            double ratio=(double)input/stored,reduction=100*(1-(double)stored/input);
            csv.Add(string.Join(",",new object?[]{application,pid,round,backend,codec,blockBytes,pages,input,stored,kind,ratio,reduction,encode50,encode95,encode99,restore50,restore95,restore99,true,null}.Select(F)));
        }
        foreach(var c in result.GetProperty("codec_comparison").GetProperty("codecs").EnumerateArray())
        {
            if(c.GetProperty("status").GetString()!="serialized_roundtrip_checked")continue;
            Row("user_mode_sample",c.GetProperty("codec").GetString()!,c.GetProperty("block_bytes").GetInt32(),c.GetProperty("test_pages").GetInt32(),c.GetProperty("input_bytes").GetInt64(),c.GetProperty("total_with_reserve_bytes").GetInt64(),"serialized_plus_model_reserve_not_RSS",c.GetProperty("compress_p50_us_per_block").GetDouble(),c.GetProperty("compress_p95_us_per_block").GetDouble(),null,c.GetProperty("stored_payload_restore_p50_us_per_block").GetDouble(),c.GetProperty("stored_payload_restore_p95_us_per_block").GetDouble(),null);
        }
        if(result.TryGetProperty("kernel_comparison",out var k)&&k.ValueKind==JsonValueKind.Object)
        foreach(var row in k.GetProperty("rows").EnumerateArray())
        {
            var full=row.GetProperty("full");long input=full.GetProperty("logical_bytes").GetInt64(),stored=full.GetProperty("session_allocation_bytes").GetInt64();
            Row("kernel_copied_sample",row.GetProperty("mode").GetString()!,4096,row.GetProperty("pages").GetInt32(),input,stored,"session_requested_pool_not_RSS",row.GetProperty("put_p50_us").GetDouble(),row.GetProperty("put_p95_us").GetDouble(),row.GetProperty("put_p99_us").GetDouble(),row.GetProperty("warm_get_p50_us").GetDouble(),row.GetProperty("warm_get_p95_us").GetDouble(),row.GetProperty("warm_get_p99_us").GetDouble());
            text.AppendLine(FormattableString.Invariant($"  Kernel {row.GetProperty("mode").GetString()}: payload {full.GetProperty("payload_bytes").GetInt64()} B; session requested {stored} B; full-byte reads {row.GetProperty("verified_reads").GetInt32()}; warmed GET p99 {row.GetProperty("warm_get_p99_us").GetDouble():F2} us/page. НЕ освобождённая RAM приложения."));
        }
    }

}
