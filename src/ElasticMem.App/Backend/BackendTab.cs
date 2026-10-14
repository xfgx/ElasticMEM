using System.Diagnostics;
using System.Text.Json;
using ElasticMem.Core.Backend;
using ElasticMem.Core.Research;
namespace ElasticMem.App.Backend;
internal sealed class BackendTab:TabPage
{
    readonly BackendController controller=new();
    readonly TextBox vm=new(){Width=200,PlaceholderText="Hyper-V VM name"},host=new(){Width=230,PlaceholderText="RDS host FQDN / IPv4"},collection=new(){Width=180,PlaceholderText="RDS collection"};
    readonly NumericUpDown min=N(1024,65536,2048),startup=N(1024,65536,4096),max=N(1024,65536,8192),buffer=N(5,80,20);
    readonly CheckBox auto=new(){Text="Запускать выбранную VM при старте backend-агента",AutoSize=true};
    readonly Label status=Ui.Hint("Backend не настроен. Запустите ElasticMem elevated для Hyper-V/PowerShell Direct. Рабочий стол хоста не заменяется.");
    readonly TextBox output=new(){Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,WordWrap=false,Dock=DockStyle.Fill,Font=new Font("Consolas",9)};
    readonly DataGridView processes=new(){ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,MultiSelect=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,Height=180,Width=920};
    readonly List<Control> controls=new();readonly System.Windows.Forms.Timer poll=new(){Interval=15000};
    readonly Func<bool> otherWork;
    JsonElement? current,baseline;BackendProfile? saved;bool busy;Task? work;
    public bool IsBusy=>busy;
    static NumericUpDown N(int lo,int hi,int value)=>new(){Minimum=lo,Maximum=hi,Value=value,Width=90};
    public BackendTab(Func<bool> otherWork):base("Backend VM")
    {
        this.otherWork=otherWork;
        var stack=new VStack{Padding=new Padding(16)};
        stack.Add(Ui.Hint("Процессы живут в отдельной Windows VM; окна — через поддерживаемый RDS RemoteApp. Память: guest Windows Compression + Hyper-V Dynamic Memory. Наш SYS НЕ пейджер VMM. Нет обещания экономии RAM: локальная VM имеет собственный overhead."));
        stack.Add(Ui.Row(Ui.L("VM"),vm,Ui.L("RemoteApp host"),host,Ui.L("Collection"),collection));
        stack.Add(Ui.Row(Ui.L("Min MiB"),min,Ui.L("Startup MiB"),startup,Ui.L("Max MiB"),max,Ui.L("Buffer %"),buffer));
        var help=new Button{Text="Инструкция Backend VM",AutoSize=true};help.Click+=(_,_)=>ElasticMem.App.Research.ResearchHelp.Show(FindForm()!);
        stack.Add(Ui.Row(auto,help));controls.AddRange(new Control[]{vm,host,collection,min,startup,max,buffer,auto});
        Button B(string text,Func<Task> action){var b=new Button{Text=text,AutoSize=true};b.Click+=async(_,_)=>await Perform(action);controls.Add(b);return b;}
        var createVm=B("Создать Windows VM автоматически…",async()=>{
            var info=await controller.RunCreation("HostInfo");Show(info);if(!Passed(info))return;
            string proposed=vm.Text.Trim();if(proposed==""||info.GetProperty("vm_names").EnumerateArray().Any(n=>string.Equals(n.GetString(),proposed,StringComparison.OrdinalIgnoreCase)))proposed="ElasticMem-Backend-"+DateTime.Now.ToString("yyyyMMdd-HHmmss");
            using var wizard=new CreateVmDialog(info,proposed,(int)min.Value,(int)startup.Value,(int)max.Value,(int)buffer.Value);
            if(wizard.ShowDialog(FindForm())!=DialogResult.OK||wizard.Plan==null)return;
            status.Text="Проверка SHA256 образа… затем копирование, создание и запуск VM. Не закрывайте приложение.";
            var request=await controller.HashCreationMedia(wizard.Plan);
            var result=await controller.RunCreation("CreateVm",request);Show(result);
            if(result.GetProperty("status").GetString()!="created_running_guest_unverified")return;
            var createdProfile=VmCreationOutcome.Accept(request,result,collection.Text.Trim(),auto.Checked);controller.Save(createdProfile);saved=createdProfile;
            vm.Text=saved.VmName;host.Text=saved.RdpHost;collection.Text=saved.CollectionName;baseline=null;current=null;processes.Rows.Clear();
            status.Text="VM создана, запущена и привязана по GUID. Guest OS ещё не подтверждена. Откройте консоль; затем укажите RDP hostname/IP и сохраните учётную запись нового гостя. Пароль предыдущей VM не используется.";
            var console=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"vmconnect.exe")){UseShellExecute=false};console.ArgumentList.Add("localhost");console.ArgumentList.Add(saved.VmName);try{Process.Start(console);}catch{status.Text+=" VMConnect не открыт; откройте уже созданную VM через Hyper-V Manager. Не создавайте повторную VM вслепую.";}
        });
        stack.Add(Ui.Row(createVm));
        var pair=B("1 · Сохранить / привязать VM",async()=>{
            var candidate=new BackendProfile(vm.Text.Trim(),host.Text.Trim(),collection.Text.Trim(),(int)min.Value,(int)startup.Value,(int)max.Value,(int)buffer.Value,AutoStartVm:auto.Checked);candidate.Validate();
            if(!Confirm("Будет привязана VM «"+candidate.VmName+"». Управление относится только к её GUID. Готовая Windows VM/RDS должна уже существовать. Продолжить?"))return;
            controller.Save(candidate);var r=await controller.Run("Probe");Show(r);
            if(Passed(r)){saved=candidate with{VmId=r.GetProperty("vm_id").GetString()!};controller.Save(saved);baseline=null;current=null;processes.Rows.Clear();}
        });
        var credential=B("2 · Учётная запись гостя…",async()=>{if(Confirm("Введите admin-пароль ВНУТРИ гостевой VM в системном защищённом запросе. Он сохраняется Windows DPAPI для текущего пользователя/ПК, не в логах. RDP-вход отдельно. Продолжить?"))Show(await controller.Run("SaveCredential"));});
        var memory=B("3 · Dynamic Memory (VM выключена)",async()=>{if(Confirm("Изменить min/startup/max/buffer только привязанной выключенной VM? Это Hyper-V, не настройка драйвера. Сначала сохраните профиль."))Show(await controller.Run("ConfigureMemory"));});
        var start=B("Запустить VM",async()=>Show(await controller.Run("StartVm")));
        var stop=B("Штатно выключить VM",async()=>{if(Confirm("Все гостевые приложения завершатся. Запросить штатное выключение VM без TurnOff/force?"))Show(await controller.Run("ShutdownVm"));});
        stack.Add(Ui.Row(pair,credential,memory)).Add(Ui.Row(start,stop));
        var compression=B("4 · Включить сжатие Windows в госте",async()=>{if(Confirm("Enable-MMAgent -MemoryCompression только ВНУТРИ привязанной VM. Политика host не меняется. У Windows нет гарантии сжатия конкретного PID. Продолжить?"))Show(await controller.Run("EnableCompression"));});
        var publish=B("5 · Опубликовать приложения RemoteApp",async()=>{if(Confirm("Опубликовать allowlisted system-wide Edge/Chrome/Telegram в существующей RDS collection этого единственного гостевого Session Host? Нужны настроенные домен/RDS/лицензии. Windows11 registry patches не используются."))Show(await controller.Run("PublishApps"));});
        stack.Add(Ui.Row(compression,publish));
        var sysTest=B("SYS · сжатие → освобождение → восстановление (свой буфер)",async()=>{
            if(!Confirm("Только изолированная тестовая VM с новым подписанным SYS. Проверка 64 собственных страниц ElasticMem: XPRESS в SYS, VirtualFree decommit, версия/полное восстановление. Это НЕ память гостевой VM и НЕ Chrome/Telegram. Продолжить?"))return;
            string root=Path.Combine(AppSettings.Dir,"sys-pager-reports");
            using var run=new ResearchRun(root,"sys-owned-pager",Updater.Current.ToString(),"local",new{scope="synthetic owned native backing",foreign_memory=false});
            try{var result=await Task.Run(()=>ElasticMem.DriverClient.PagerSmoke.Run());run.WriteJson("result.json",result);run.Seal("passed_owned_fixture_only","SYS synthetic owned buffer; NOT Windows guest pager; physical RAM saving unproven");Show(JsonSerializer.SerializeToElement(result));}
            catch(Exception ex){run.WriteJson("result.json",new{status="failed_no_valid_result",error_class=ex.GetType().Name,physical_ram_saved_bytes=(long?)null});run.Seal("failed_no_valid_result","No native SYS paging confirmation");throw;}
            status.Text+=" · Отчёт: "+run.DirectoryPath;
        });
        stack.Add(Ui.Row(sysTest));
        var refresh=B("Обновить процессы / память",RefreshBackend);
        var saveBaseline=B("Точка сравнения",()=>{if(!current.HasValue||!Passed(current.Value))throw new InvalidOperationException("Сначала успешное обновление работающей VM");baseline=current.Value.Clone();status.Text="Baseline сохранён для этой VM/boot. Это не причинный A/B.";return Task.CompletedTask;});
        var export=B("ZIP логов без credentials",()=>{string zip=controller.Export(baseline,current);status.Text=zip;Process.Start(new ProcessStartInfo("explorer.exe",Path.GetDirectoryName(zip)!){UseShellExecute=true});return Task.CompletedTask;});
        stack.Add(Ui.Row(refresh,saveBaseline,export));
        Button Launch(string app)=>B("Открыть "+app+" на хосте",async()=>{await RefreshBackend();if(!current.HasValue||!Passed(current.Value))throw new InvalidOperationException("Backend недоступен");controller.LaunchRemoteApp(app,current.Value);status.Text="Открыт mstsc RemoteApp. Подтвердите обычный RDP-вход. Факт запуска приложения — в следующем списке гостевых процессов.";});
        var kill=B("Завершить выбранный ГОСТЕВОЙ PID",async()=>{
            if(processes.CurrentRow?.Tag is not JsonElement row)throw new InvalidOperationException("Выберите процесс");
            if(!current.HasValue||!Passed(current.Value)||current.Value.GetProperty("vm_id").GetString()!=controller.Load()?.VmId)throw new InvalidOperationException("Обновите список для текущей привязанной VM");
            int pid=row.GetProperty("pid").GetInt32();if(!Confirm("Принудительно завершить только гостевой "+row.GetProperty("name").GetString()+" PID "+pid+"? Несохранённые данные могут потеряться. Host PID не затрагиваются."))return;
            Show(await controller.Run("StopProcess",pid,row.GetProperty("start_ticks").GetInt64(),row.GetProperty("session_id").GetInt32()));await RefreshBackend();
        });
        stack.Add(Ui.Row(Launch("edge"),Launch("chrome"),Launch("telegram"),kill));
        foreach(var name in new[]{"Guest PID","Приложение","Session","Working set MiB","Private commit MiB"})processes.Columns.Add(name,name);
        stack.Add(processes).Add(status).Add(output,true);Controls.Add(stack);
        try{saved=controller.Load();if(saved!=null){vm.Text=saved.VmName;host.Text=saved.RdpHost;collection.Text=saved.CollectionName;min.Value=saved.MinimumMiB;startup.Value=saved.StartupMiB;max.Value=saved.MaximumMiB;buffer.Value=saved.MemoryBufferPercent;auto.Checked=saved.AutoStartVm;}}catch{status.Text="Существующий профиль не прочитан/невалиден; настройте заново.";}
        poll.Tick+=async(_,_)=>{if(!busy&&saved!=null&&Enabled&&!otherWork())await Perform(RefreshBackend,false);};poll.Start();
        Disposed+=(_,_)=>{poll.Stop();poll.Dispose();};
    }
    static bool Confirm(string text)=>MessageBox.Show(text,"ElasticMem backend · согласие",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)==DialogResult.Yes;
    static bool Passed(JsonElement r)=>r.TryGetProperty("status",out var s)&&s.GetString()=="passed";
    void Show(JsonElement result){output.Text=JsonSerializer.Serialize(result,ResearchRun.Json);status.Text=result.TryGetProperty("status",out var s)?s.GetString():"unknown";}
    async Task RefreshBackend()
    {
        var r=await controller.Run("Status");current=r.Clone();Show(r);processes.Rows.Clear();
        if(!Passed(r)||!r.TryGetProperty("guest",out var g))return;
        foreach(var p in g.GetProperty("processes").EnumerateArray()){
            int i=processes.Rows.Add(p.GetProperty("pid").GetInt32(),p.GetProperty("name").GetString(),p.GetProperty("session_id").GetInt32(),(p.GetProperty("working_set_bytes").GetInt64()/1048576d).ToString("F1"),(p.GetProperty("private_commit_bytes").GetInt64()/1048576d).ToString("F1"));processes.Rows[i].Tag=p.Clone();
        }
        status.Text=$"VM assigned: {r.GetProperty("assigned_bytes").GetInt64()/1048576d:F0} MiB; demand: {r.GetProperty("demand_bytes").GetInt64()/1048576d:F0} MiB; host available: {r.GetProperty("host_available_bytes").GetInt64()/1048576d:F0} MiB; Windows Compression: {g.GetProperty("memory_compression_enabled")}. Не измерено: per-process physical saving.";
        if(baseline.HasValue)try{output.AppendText("\r\n"+JsonSerializer.Serialize(BackendMeasurements.Compare(baseline.Value,r),ResearchRun.Json));}catch{baseline=null;status.Text+=" Baseline сброшен: VM/boot/state изменились.";}
    }
    async Task Perform(Func<Task> action,bool alert=true)
    {
        if(busy)return;
        if(otherWork()){if(alert)MessageBox.Show("Остановите текущий агент/тест перед управлением backend.","Backend");return;}
        busy=true;foreach(var c in controls)c.Enabled=false;
        try{work=action();await work;}
        catch(Exception ex){status.Text="Операция не подтверждена: "+ex.GetType().Name;if(alert)MessageBox.Show(ex.Message,"Backend: не выполнено",MessageBoxButtons.OK,MessageBoxIcon.Warning);}
        finally{work=null;busy=false;if(!IsDisposed)foreach(var c in controls)c.Enabled=true;}
    }
    public async Task AgentStarted()
    {
        if(saved!=null&&saved.AutoStartVm)await Perform(async()=>Show(await controller.Run("StartVm")),false);
        if(saved!=null)await Perform(RefreshBackend,false);
    }
    public async Task WaitAndStop(){poll.Stop();if(work!=null)await work;}
}
