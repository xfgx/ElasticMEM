using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using System.Runtime.InteropServices;
using ElasticMem.Core.Research;
namespace ElasticMem.App.Research;
internal sealed record AutoStep(string Check,string Status,string Folder,string Note);
internal sealed record AutoOutcome(string RunsRoot,string LastRun,string Archive,string Report,string Status);
internal static class AutoResearch
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder name,ref uint size);
    static void EnsureNoOtherInstances()
    {
        using var self=Process.GetCurrentProcess();foreach(var other in Process.GetProcessesByName(self.ProcessName))using(other)if(other.Id!=Environment.ProcessId)throw new InvalidOperationException("Закройте другие экземпляры ElasticMem: состояние их агентов неизвестно. Проверка экземпляров не является глобальной блокировкой старых агентов.");
    }
    internal static void ConfirmRenderer(IntPtr h,int pid,string name,long startTicks)
    {
        if(!Native.GetProcessTimes(h,out long creation,out long exit,out _,out _)||exit!=0||DateTime.FromFileTimeUtc(creation).Ticks!=startTicks)throw new InvalidOperationException("Renderer process lifetime changed");
        var image=new StringBuilder(32768);uint length=32768;if(!QueryFullProcessImageName(h,0,image,ref length)||!Path.GetFileNameWithoutExtension(image.ToString()).Equals(name,StringComparison.OrdinalIgnoreCase)||!new[]{"chrome","msedge","steamwebhelper"}.Contains(name,StringComparer.OrdinalIgnoreCase))throw new NotSupportedException("Image is not an allowed renderer family");
        if(!Native.IsWow64Process2(h,out var pm,out var nm)||pm!=0||nm!=0x8664)throw new NotSupportedException("Renderer must be native x64");
        string? command=Native.CommandLine(h);if(command==null||!Regex.IsMatch(command,@"(?:^|\s)--type=renderer(?:\s|$)"))throw new NotSupportedException("Renderer role not confirmed; no trim performed");
        using var self=Process.GetCurrentProcess();if(!Native.ProcessIdToSessionId(pid,out int session)||session!=self.SessionId||!Native.GetProcessTimes(h,out var final,out var stopped,out _,out _)||final!=creation||stopped!=0)throw new InvalidOperationException("Renderer session/lifetime changed");
    }
    internal static ProcessSnapshot CaptureEpochSnapshot(int pid,long expected,int mib,ResearchRun run,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Gone/reused identities are never replaced with a newly discovered PID.
        using var p=Process.GetProcessById(pid);
        IntPtr h=Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION|Native.PROCESS_VM_READ,false,pid);
        if(h==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Fresh epoch target handle unavailable");
        try
        {
            string name=p.ProcessName;ConfirmRenderer(h,pid,name,expected);
            var snapshot=WindowsCapture.Capture(pid,mib,run,ct,expected);
            ConfirmRenderer(h,pid,name,expected);return snapshot;
        }
        finally{Native.CloseHandle(h);}
    }
    static int[] CounterPids()
    {
        var list=new List<(int Pid,string Name,long Bytes)>();using var self=Process.GetCurrentProcess();
        foreach(var p in Process.GetProcesses())using(p)try
        {
            if(p.Id<=4||p.Id==Environment.ProcessId||p.SessionId!=self.SessionId||
               !new[]{"chrome","msedge","steam","steamwebhelper","python","pythonw","node"}.Contains(p.ProcessName,StringComparer.OrdinalIgnoreCase))continue;
            list.Add((p.Id,p.ProcessName,p.WorkingSet64));
        }catch{}
        // Read-only observations may cover heterogeneous ordinary apps.
        // NEVER use this wider list for snapshots or EmptyWorkingSet.
        var ordered=list.GroupBy(p=>p.Name,StringComparer.OrdinalIgnoreCase).OrderByDescending(g=>g.Sum(p=>p.Bytes)).ToArray();
        return ordered.Select(g=>g.OrderByDescending(p=>p.Bytes).First().Pid)
            .Concat(list.OrderByDescending(p=>p.Bytes).Select(p=>p.Pid)).Distinct().Take(5).ToArray();
    }
    public static ResearchCandidate[] Discover()
    {
        var result=new List<ResearchCandidate>();using var self=Process.GetCurrentProcess();int session=self.SessionId;
        foreach(var p in Process.GetProcesses())using(p)
        {
            IntPtr h=IntPtr.Zero;
            try
            {
                string name=p.ProcessName;if(p.Id==Environment.ProcessId||p.Id<=4||p.SessionId!=session||!new[]{"chrome","msedge","steamwebhelper"}.Contains(name,StringComparer.OrdinalIgnoreCase))continue;
                long expected=p.StartTime.ToUniversalTime().Ticks;
                h=Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION|Native.PROCESS_VM_READ,false,p.Id);if(h==IntPtr.Zero)continue;
                if(!Native.GetProcessTimes(h,out long creation,out long exit,out _,out _)||exit!=0||DateTime.FromFileTimeUtc(creation).Ticks!=expected)continue;
                var image=new StringBuilder(32768);uint imageLength=32768;
                if(!QueryFullProcessImageName(h,0,image,ref imageLength)||!Path.GetFileNameWithoutExtension(image.ToString()).Equals(name,StringComparison.OrdinalIgnoreCase))continue;
                if(!Native.ProcessIdToSessionId(p.Id,out int actualSession)||actualSession!=session)continue;
                if(!Native.IsWow64Process2(h,out var pm,out var nm)||pm!=0||nm!=0x8664)continue;
                string? command=Native.CommandLine(h);bool renderer=command!=null&&Regex.IsMatch(command,@"(?:^|\s)--type=renderer(?:\s|$)");
                // Command lines can contain private data: never persisted, only this role predicate survives.
                if(!Native.GetProcessTimes(h,out long finalCreation,out long finalExit,out _,out _)||finalCreation!=creation||finalExit!=0)continue;
                if(renderer)result.Add(new(p.Id,name,DateTime.FromFileTimeUtc(creation).Ticks,p.WorkingSet64,true,true,true));
            }
            catch{ /* inaccessible/exited/unknown role are excluded, never guessed */ }
            finally{if(h!=IntPtr.Zero)Native.CloseHandle(h);}
        }
        return result.ToArray();
    }
    public static async Task<AutoOutcome> RunAsync(bool memoryConsent,int seconds,IProgress<string>? progress,CancellationToken ct,Func<string,CancellationToken,Task<string>>? uiAudit=null,bool allowTrim=false,bool extended=false,string? scratch=null)
    {
        string batch=Path.Combine(AppSettings.Dir,"research-batches",DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")+"-"+Guid.NewGuid().ToString("N")[..8]);string root=Path.Combine(batch,"runs");Directory.CreateDirectory(root);
        var steps=new List<AutoStep>();var selection=Array.Empty<ResearchCandidate>();var g1Selection=Array.Empty<ResearchCandidate>();int[] counterPids=Array.Empty<int>();
        using var summary=new ResearchRun(root,"auto-summary",Updater.Current.ToString(),ResearchJobs.Commit,new{seconds,memory_consent=memoryConsent,working_set_trim_consent=allowTrim,extended,selection="same-session native x64 Chromium renderers; one family; 2..4; refresh before G1",snapshot_mib=16,pool_mib=32,iterations=100,screenshots=false});
        string archive=Path.Combine(batch,"ElasticMem-logs-"+Path.GetFileName(batch)+".zip");
        async Task<string> Step(string key,ResearchOptions options,string note)
        {
            ct.ThrowIfCancellationRequested();EnsureNoOtherInstances();progress?.Report("Автопроверка: "+key);
            string folder=await Task.Run(()=>ResearchJobs.Execute(key,options,progress,ct,root));ResearchRun.Verify(folder);EnsureNoOtherInstances();
            using var m=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"manifest.json")));string status=m.RootElement.GetProperty("status").GetString()!;
            steps.Add(new(key,status,Path.GetFileName(folder),note));return folder;
        }
        void Skip(string key,string status,string note)
        {
            using var run=new ResearchRun(root,"skip-"+key,Updater.Current.ToString(),ResearchJobs.Commit,new{reason=note});run.WriteJson("result.json",new{check=key,status,reason=note,measurement_performed=false});run.Seal(status,note);ResearchRun.Verify(run.DirectoryPath);steps.Add(new(key,status,Path.GetFileName(run.DirectoryPath),note));
        }
        try
        {
            EnsureNoOtherInstances();
            var safe=new ResearchOptions(Array.Empty<int>());
            await Step("environment",safe,"Read-only; MMAgent settings are not changed.");
            string selftest=await Step("selftest",safe,"Core correctness, not scientific effectiveness.");
            if(BatchPolicy.Failure(steps[^1].Status))
            {
                foreach(var key in new[]{"g0-sample","g0-calibration","g0-model","g1","g4","g0-epochs","g5","disk-qd1","fault-origin","g4-churn","g2","gpu-probe","g3","g3-dma","ui-audit"})Skip(key,"skipped_core_selftest_failed","Core selftest failed: dependent checks not safe to claim.");
            }
            else
            {
                selection=BatchPolicy.Select(await Task.Run(Discover),Environment.ProcessId,minimum:1);
                counterPids=CounterPids();if(counterPids.Length==0)counterPids=new[]{Environment.ProcessId};
                await Step("g0-sample",safe with{Pids=counterPids,Seconds=extended?Math.Max(seconds,600):seconds},"Read-only up to5 Chrome/Edge/Steam/Python/Node process counters,when already running. This wider list is NOT used for trims or snapshots. No complete desktop/trace claim.");
                await Step("g0-calibration",safe,"Isolated own32MiB worker: baseline/trim/trim+query,rotated10rounds,descriptive paired bootstrap,measured operations/s. Not real browser throughput.");
                Skip("g0-model","skipped_exact_trace_unavailable","This build does not acquire a complete Windows access trace. DAMON/page_idle sampling must not be relabelled exact.");
                g1Selection=selection.Length==0?Array.Empty<ResearchCandidate>():BatchPolicy.Select(await Task.Run(Discover),Environment.ProcessId,selection[0].Family);
                if(!memoryConsent)Skip("g1","skipped_no_memory_read_consent","Memory capture requires explicit consent; CLI use --allow-memory-read.");
                else if(g1Selection.Length<2)Skip("g1","skipped_no_safe_process_pair","No 2..4 readable native x64 same-family renderers. Open normal Chrome/Edge tabs or Steam client and repeat; no apps are launched automatically.");
                else
                {
                    string g1=await Step("g1",safe with{Pids=g1Selection.Select(p=>p.Pid).ToArray(),SnapshotMiB=16,ExpectedStarts=g1Selection.ToDictionary(p=>p.Pid,p=>p.StartTicks)},"Fresh same-family renderer selection; residency pre/post guards; no self capture. Private snapshots remain local and are excluded from ZIP.");
                    if(!BatchPolicy.Failure(steps[^1].Status))
                    {
                        await Step("g4",safe with{SourceRun=g1},"Held-out-process codec comparison; train PID excluded from test; counted dictionary/page mappings/reserve. No live reclaim.");
                        if(allowTrim)
                        {
                            foreach(var target in g1Selection.OrderBy(p=>p.StartTicks).ThenBy(p=>p.Pid).Take(extended?4:2))
                            {
                                string fresh=await Step("epoch-snapshot",safe with{Pids=new[]{target.Pid},SnapshotMiB=16,ExpectedStarts=new Dictionary<int,long>{{target.Pid,target.StartTicks}}},"PID "+target.Pid+": recheck exact original lifetime/renderer and recapture immediately before epochs; no substitution; older selected instances first,not a stability guarantee.");
                                if(steps[^1].Status!="fresh_renderer_snapshot_checked"){Skip("g0-epochs","skipped_fresh_target_unavailable","PID "+target.Pid+": no valid fresh identity-bound snapshot; no trim or replacement attempted.");Skip("g5","skipped_epoch_source_unavailable","PID "+target.Pid+": fresh target unavailable.");continue;}
                                string epochFolder=await Step("g0-epochs",safe with{SourceRun=fresh,AllowTrim=true,Epochs=extended?600:12,Extended=extended,EpochPid=target.Pid},"PID "+target.Pid+": fresh same-lifetime snapshot,independent full planned trace; permission/exit failure does not stop other original targets. No settings change.");
                                if(!BatchPolicy.Failure(steps[^1].Status)&&steps[^1].Status=="perturbing_epoch_proxy_not_exact_trace")
                                    {
                                    await Step("g5",safe with{SourceRun=epochFolder},"PID "+target.Pid+": only completed independently sealed epoch source; train prefix/held-out suffix,never partial trace.");
                                    await Step("epochs-replay",safe with{SourceRun=epochFolder},"Long T1..256; causal cold partner blocks1/4/8/16; local input only,not RAM ceiling/GC attribution.");
                                }
                                else Skip("g5","skipped_epoch_source_unavailable","PID "+target.Pid+": complete verified planned epochs required; failed target stays failed in batch.");
                            }
                        }
                        else {Skip("g5","skipped_no_trim_consent","No epoch source; old logs-only ZIP does not contain private traces.");Skip("g0-epochs","skipped_no_trim_consent","Enable the explicit working-set-trim checkbox for this disruptive lab; ordinary button never trims target processes.");}
                    }
                    else{Skip("g5","skipped_g1_source_failed","No epoch source");Skip("g4","skipped_g1_source_failed","Successful resident snapshot required");Skip("g0-epochs","skipped_g1_source_failed","Successful resident snapshot required");}
                }
                if(!memoryConsent||g1Selection.Length<2){Skip("g5","skipped_g1_source_unavailable","No epoch source");Skip("g4","skipped_g1_source_unavailable","No successful resident G1 source");Skip("g0-epochs","skipped_g1_source_unavailable","No successful resident G1 source");}
                Skip("fault-origin","skipped_fault_origin_not_attributed","Post-trim own reads have unknown backing. No confirmed Xpress/pagefile latency; no pressure induced or system settings changed.");
                Skip("g4-churn","skipped_volume_decision_unavailable","Own synthetic churn available as manual control; real renderer churn/driver decision requires independent volume evidence.");
                await Step("disk-qd1",safe with{Scratch=scratch??Path.GetTempPath()},"Own32MiB unbuffered random file QD1,4/64/256KiB; runs without GPU. NOT real pagefile fault or verified NVMe.");
                await Step("environment",safe,"Second read-only environment/store-visibility probe; WS is not compressed payload.");
                await Step("g2",safe,"Own fixed-schema records; serialized footprint, not CPython/V8 RSS.");
                string gpu=await Step("gpu-probe",safe,"Software and UMA adapters are never used as VRAM evidence.");
                int adapter=-1,dmaAdapter=-1;
                if(!BatchPolicy.Failure(steps[^1].Status)&&File.Exists(Path.Combine(gpu,"result.json")))
                {
                    using var d=JsonDocument.Parse(File.ReadAllText(Path.Combine(gpu,"result.json")));
                    foreach(var a in d.RootElement.GetProperty("adapters").EnumerateArray())if(a.TryGetProperty("software",out var sw)&&!sw.GetBoolean()&&a.TryGetProperty("uma",out var uma)&&!uma.GetBoolean()){if(dmaAdapter<0)dmaAdapter=a.GetProperty("index").GetInt32();if(adapter<0&&a.TryGetProperty("gpu_upload_supported",out var supported)&&supported.GetBoolean())adapter=a.GetProperty("index").GetInt32();}
                }
                if(adapter<0)Skip("g3","unsupported_no_measurement","No supported discrete GPU_UPLOAD adapter. RX570/unsupported hardware is skipped; software GPU is not a replacement.");
                else await Step("g3",safe with{Adapter=adapter,Scratch=Path.GetTempPath(),Negative=true,PoolMiB=32,Iterations=100},"Own buffers + 32 MiB pool + negative checks. Disk path is system TEMP; NVMe/SSD type is not verified. Not a pagefile-fault measurement or VRAM pager.");
                if(dmaAdapter<0)Skip("g3-dma","unsupported_no_measurement","No discrete D3D12 adapter; software/UMA excluded.");
                else await Step("g3-dma",safe with{Adapter=dmaAdapter,Iterations=100,PoolMiB=32},"DEFAULT→READBACK copy queue;4/64/256KiB restore and roundtrip; GPU_UPLOAD/ReBAR not required. Own buffers,not pagefile.");
                if(uiAudit==null)Skip("ui-audit","skipped_no_gui_context","CLI cannot audit the rendered WinForms interface; use the application button.");
                else
                {
                    ct.ThrowIfCancellationRequested();progress?.Report("Автопроверка: интерфейс двух тем");string folder=await uiAudit(root,ct);ResearchRun.Verify(folder);
                    using var m=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"manifest.json")));steps.Add(new("ui-audit",m.RootElement.GetProperty("status").GetString()!,Path.GetFileName(folder),"Mechanical audit at current DPI, no screenshots, not human visual signoff."));
                }
            }
        }
        catch(OperationCanceledException ex){steps.Add(new("batch","cancelled_partial_not_result","",ex.Message));}
        catch(Exception ex){summary.Log(ex.ToString());steps.Add(new("batch","failed_automation","",ex.Message));}
        string conclusion=BatchPolicy.Conclusion(steps.Select(s=>s.Status));
        string report="ElasticMem "+Updater.Current+" — автоматическая проверка\r\n\r\nСтатус: "+conclusion+"\r\n\r\n"+string.Join("\r\n",steps.Select(s=>$"{s.Check}: {s.Status}\r\n  {s.Note}"))+"\r\n\r\nГ0 PID: "+string.Join(", ",selection.Select(x=>$"{x.Family} ({x.Pid})"))+"\r\nГ1 PID: "+string.Join(", ",g1Selection.Select(x=>$"{x.Family} ({x.Pid})"))+"\r\n\r\nФайлы: "+batch+"\r\nПланируемый ZIP без дампов, контейнеров, трасс и screenshots: "+archive+"\r\nНичего не отправлено по сети. Перед отправкой просмотрите архив.\r\nГ0-модель/VM-стенд, kernel pager, семантическая нормализация, CPython/V8 патч и CPU/AVX-путь Г3 не реализованы.\r\n";
        report=report.Replace("Г0 PID: "+string.Join(", ",selection.Select(x=>$"{x.Family} ({x.Pid})")),"Г0 счётчики PID: "+string.Join(", ",counterPids));
        var numericErrors=new List<string>();
        try{report+=OneClickReport.Read(root,numericErrors,summary.DirectoryPath);}catch(Exception ex){summary.Log("Numeric report error: "+ex);numericErrors.Add(ex.GetType().Name);}
        if(numericErrors.Count>0)
        {
            steps.Add(new("numeric-report","failed_numeric_summary","",string.Join("; ",numericErrors)));
            string previous=conclusion;conclusion=BatchPolicy.Conclusion(steps.Select(s=>s.Status));
            report=report.Replace("Статус: "+previous,"Статус: "+conclusion)+"\r\nЧИСЛОВАЯ СВОДКА С ОШИБКАМИ; см. диагностические прогоны.\r\n";
        }
        summary.WriteJson("result.json",new{schema="elasticmem.auto.v1",status=conclusion,steps,selection,g1_selection=g1Selection,privacy="No uploads; raw inputs/snapshots/containers/screenshots excluded from generated ZIP; logs not guaranteed anonymous",limitations=new[]{"No new transparent optimizer or memory reclaim enabled","G0 sampled counters, not full trace/rigorous ceiling","G3 includes DEFAULT DMA and optional GPU_UPLOAD; fixed small pools,not durable pager","UI mechanical audit, not visual quality signoff"}});
        File.WriteAllText(summary.FilePath("auto-report.txt"),report);summary.Seal(conclusion,"Automatic batch. See result.json/auto-report.txt. Skips/failures are retained, not claimed successful.");ResearchRun.Verify(summary.DirectoryPath);
        try{await Task.Run(()=>ResearchRun.ExportLogs(root,archive,false));}
        catch(Exception ex){report+="\r\nZIP НЕ СОЗДАН: "+ex.Message;if(File.Exists(archive))try{File.Move(archive,archive+".invalid");}catch{} archive="";conclusion="failed_export_not_result";}
        return new(root,summary.DirectoryPath,archive,report,conclusion);
    }
}
