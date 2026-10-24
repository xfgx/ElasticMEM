using System.Diagnostics;
using System.Text.Json;
using ElasticMem.Core.Backend;
using ElasticMem.Core.Research;
namespace ElasticMem.App.Backend;
internal sealed class BackendTab:TabPage
{
    readonly BackendController controller=new();
    sealed record ProfileChoice(string Id,string Text){public override string ToString()=>Text;}
    sealed record KindChoice(VmBackendKind Kind,string Text){public override string ToString()=>Text;}
    readonly ComboBox profiles=new(){Width=430,DropDownStyle=ComboBoxStyle.DropDownList},newKind=new(){Width=260,DropDownStyle=ComboBoxStyle.DropDownList};
    readonly List<Control> hypervOnly=new(),sysOnly=new();bool loadingProfiles;
    readonly NumericUpDown sysCapacity=N(64,1048576,8192),sysQuota=N(1,32,16);

    readonly TextBox vm=new(){Width=200,PlaceholderText="Имя профиля / VM"},host=new(){Width=230,PlaceholderText="RDS host FQDN / IPv4"},collection=new(){Width=180,PlaceholderText="RDS collection"};
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
    public BackendTab(Func<bool> otherWork):base("VM · SYS / Hyper-V")
    {
        this.otherWork=otherWork;
        var stack=new VStack{Padding=new Padding(16)};
        stack.Add(Ui.Hint("Тип1 — свой WHP VMM + SYS (экспериментально): только synthetic guest, Windows boot/UEFI/устройства НЕ реализованы. WHP требует гипервизор Windows. Тип2 — существующий Hyper-V backend (не SYS pager). Нет fallback. Экономия RAM приложений не подтверждена."));
        stack.Add(Ui.Row(Ui.L("VM"),vm,Ui.L("RemoteApp host"),host,Ui.L("Collection"),collection));
        stack.Add(Ui.Row(Ui.L("Min MiB"),min,Ui.L("Startup MiB"),startup,Ui.L("Max MiB"),max,Ui.L("Buffer %"),buffer));
        var help=new Button{Text="Инструкция Backend VM",AutoSize=true};help.Click+=(_,_)=>ElasticMem.App.Research.ResearchHelp.Show(FindForm()!);
        stack.Add(Ui.Row(auto,help));controls.AddRange(new Control[]{vm,host,collection,min,startup,max,buffer,auto});
        newKind.Items.Add(new KindChoice(VmBackendKind.Sys,"Тип1 · свой VMM + SYS (экспериментально)"));newKind.Items.Add(new KindChoice(VmBackendKind.HyperV,"Тип2 · Hyper-V (существующий backend)"));newKind.SelectedIndex=0;
        profiles.SelectedIndexChanged+=async(_,_)=>{if(loadingProfiles||busy||profiles.SelectedItem is not ProfileChoice selected)return;await Perform(()=>{controller.SelectProfile(selected.Id);ReloadProfiles();return Task.CompletedTask;});};
        Button B(string text,Func<Task> action){var b=new Button{Text=text,AutoSize=true};b.Click+=async(_,_)=>await Perform(action);controls.Add(b);return b;}
        var addProfile=B("Новый профиль выбранного типа",()=>{
            var kind=((KindChoice)newKind.SelectedItem!).Kind;
            if(!Confirm("Добавить отдельный профиль типа "+(int)kind+"? Тип1 не создаёт работающую VM: собственный VMM ещё не готов. Тип существующей VM не конвертируется."))return Task.CompletedTask;
            string name="ElasticMem-"+(kind==VmBackendKind.Sys?"SYS":"HyperV")+"-"+(controller.Catalog().Entries.Count+1);
            controller.NewProfile(new BackendProfile(name,"unconfigured.invalid","ElasticMem",BackendKind:kind));ReloadProfiles();return Task.CompletedTask;
        });
        controls.AddRange(new Control[]{profiles,newKind,sysCapacity,sysQuota});stack.Add(Ui.Row(Ui.L("Профили VM"),profiles,Ui.L("Тип нового"),newKind,addProfile));
        var createVm=B("Создать Windows VM автоматически…",async()=>{
            if(controller.Load()?.BackendKind==VmBackendKind.Sys){Show(await controller.Run("Probe"));status.Text="Тип1: SYS не является гипервизором. Создание/запуск Windows-VM ещё не реализованы; Hyper-V не вызывался.";return;}
            var info=await controller.RunCreation("HostInfo");Show(info);if(!Passed(info))return;
            string proposed=vm.Text.Trim();if(proposed==""||info.GetProperty("vm_names").EnumerateArray().Any(n=>string.Equals(n.GetString(),proposed,StringComparison.OrdinalIgnoreCase)))proposed="ElasticMem-Backend-"+DateTime.Now.ToString("yyyyMMdd-HHmmss");
            using var wizard=new CreateVmDialog(info,proposed,(int)min.Value,(int)startup.Value,(int)max.Value,(int)buffer.Value);
            if(wizard.ShowDialog(FindForm())!=DialogResult.OK||wizard.Plan==null)return;
            status.Text="Проверка SHA256 образа… затем копирование, создание и запуск VM. Не закрывайте приложение.";
            var request=await controller.HashCreationMedia(wizard.Plan);
            var result=await controller.RunCreation("CreateVm",request);Show(result);
            if(result.GetProperty("status").GetString()!="created_running_guest_unverified")return;
            var createdProfile=VmCreationOutcome.Accept(request,result,collection.Text.Trim(),auto.Checked);controller.Save(createdProfile);saved=createdProfile;ReloadProfiles();
            vm.Text=saved.VmName;host.Text=saved.RdpHost;collection.Text=saved.CollectionName;baseline=null;current=null;processes.Rows.Clear();
            status.Text="VM создана, запущена и привязана по GUID. Guest OS ещё не подтверждена. Откройте консоль; затем укажите RDP hostname/IP и сохраните учётную запись нового гостя. Пароль предыдущей VM не используется.";
            var console=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"vmconnect.exe")){UseShellExecute=false};console.ArgumentList.Add("localhost");console.ArgumentList.Add(saved.VmName);try{Process.Start(console);}catch{status.Text+=" VMConnect не открыт; откройте уже созданную VM через Hyper-V Manager. Не создавайте повторную VM вслепую.";}
        });
        hypervOnly.Add(createVm);
        var notBoot=new Button{Text="Свой VMM · Windows boot — не реализовано",AutoSize=true,Enabled=false};
        stack.Add(Ui.Row(createVm,notBoot));
        var pair=B("1 · Сохранить / привязать VM",async()=>{
            var kind=controller.Load()?.BackendKind??throw new InvalidOperationException("Сначала создайте профиль выбранного типа.");
            var candidate=new BackendProfile(vm.Text.Trim(),kind==VmBackendKind.Sys?"unconfigured.invalid":host.Text.Trim(),kind==VmBackendKind.Sys?"ElasticMem":collection.Text.Trim(),(int)min.Value,(int)startup.Value,(int)max.Value,(int)buffer.Value,AutoStartVm:kind==VmBackendKind.HyperV&&auto.Checked,BackendKind:kind,SysStoreCapacity:(uint)sysCapacity.Value,SysStoreQuota:(ulong)sysQuota.Value*1024*1024);candidate.Validate();
            if(kind==VmBackendKind.Sys){controller.Save(candidate);ReloadProfiles();Show(await controller.Run("Probe"));return;}
            if(!Confirm("Будет привязана VM «"+candidate.VmName+"». Управление относится только к её GUID. Готовая Windows VM/RDS должна уже существовать. Продолжить?"))return;
            controller.Save(candidate);var r=await controller.Run("Probe");Show(r);
            if(Passed(r)){saved=candidate with{VmId=r.GetProperty("vm_id").GetString()!};controller.Save(saved);ReloadProfiles();baseline=null;current=null;processes.Rows.Clear();}
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
            try{var result=await Task.Run(()=>ElasticMem.DriverClient.PagerSmoke.Run(storeCapacity:(uint)sysCapacity.Value,quotaBytes:(ulong)sysQuota.Value*1024*1024));run.WriteJson("result.json",result);run.Seal("passed_owned_fixture_only","SYS synthetic owned buffer; NOT Windows guest pager; physical RAM saving unproven");Show(JsonSerializer.SerializeToElement(result));}
            catch(Exception ex){run.WriteJson("result.json",new{status="failed_no_valid_result",error_class=ex.GetType().Name,physical_ram_saved_bytes=(long?)null});run.Seal("failed_no_valid_result","No native SYS paging confirmation");throw;}
            status.Text+=" · Отчёт: "+run.DirectoryPath;
        });
        sysOnly.AddRange(new Control[]{sysCapacity,sysQuota});
        async Task RunWhp(string mode,string? store)
        {
            if(controller.Load()?.BackendKind!=VmBackendKind.Sys)throw new InvalidOperationException("Выберите профиль своего VMM, без fallback.");
            string what=mode switch{"inject-store-loss"=>"Будет инъекция отказа GET для проверки запрета resume.","host-trim"=>"Проверка: может ли сама Windows вытеснить память WHP-гостя (EmptyWorkingSet своего процесса), без SYS.",_=>store=="user"?"Native fault → restore из user-mode XPRESS хранилища, SYS не нужен.":"Native fault → SYS restore (нужен новый подписанный SYS)."};
            if(!Confirm("Только disposable Windows x64 с доступным WHP. Выполняется маленький 16-bit guest: НЕ Windows/Chrome/Telegram. Никакие security/boot настройки не меняются. "+what))return;
            Show(await WhpRunnerController.Run(mode,store));status.Text+=" · Отчёт: "+WhpRunnerController.Reports;
        }
        var whpTest=B("Свой VMM · WHP/SYS synthetic fault",()=>RunWhp("synthetic","sys"));
        var whpLoss=B("Свой VMM · инъекция отказа GET",()=>RunWhp("inject-store-loss","sys"));
        var whpUser=B("Свой VMM · WHP + user-store (без SYS)",()=>RunWhp("synthetic","user"));
        var whpTrim=B("Свой VMM · host-trim проверка",()=>RunWhp("host-trim",null));
        sysOnly.AddRange(new Control[]{whpTest,whpLoss,whpUser,whpTrim});
        var missingDevices=new Button{Text="UEFI / устройства / migration — не реализовано",Enabled=false,AutoSize=true};
        var exportWhp=B("ZIP WHP/SYS отчётов",()=>{string zip=ResearchRun.ExportLogs(WhpRunnerController.Reports,Path.Combine(AppSettings.Dir,"ElasticMem-whp-sys-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")+".zip"),false);status.Text=zip;Process.Start(new ProcessStartInfo("explorer.exe",Path.GetDirectoryName(zip)!){UseShellExecute=true});return Task.CompletedTask;});
        stack.Add(Ui.Row(whpTest,whpLoss,exportWhp,missingDevices));
        stack.Add(Ui.Row(whpUser,whpTrim));
        stack.Add(Ui.Row(Ui.L("SYS slots"),sysCapacity,Ui.L("Session quota MiB"),sysQuota,sysTest));
        hypervOnly.AddRange(new Control[]{host,collection,min,startup,max,buffer,auto,credential,memory,start,stop,compression,publish});
        var refresh=B("Обновить процессы / память",RefreshBackend);
        var saveBaseline=B("Точка сравнения",()=>{if(!current.HasValue||!Passed(current.Value))throw new InvalidOperationException("Сначала успешное обновление работающей VM");baseline=current.Value.Clone();status.Text="Baseline сохранён для этой VM/boot. Это не причинный A/B.";return Task.CompletedTask;});
        var export=B("ZIP логов без credentials",()=>{string zip=controller.Export(baseline,current);status.Text=zip;Process.Start(new ProcessStartInfo("explorer.exe",Path.GetDirectoryName(zip)!){UseShellExecute=true});return Task.CompletedTask;});
        stack.Add(Ui.Row(refresh,saveBaseline,export));
        Button Launch(string app){var button=B("Открыть "+app+" на хосте",async()=>{await RefreshBackend();if(!current.HasValue||!Passed(current.Value))throw new InvalidOperationException("Backend недоступен");controller.LaunchRemoteApp(app,current.Value);status.Text="Открыт mstsc RemoteApp. Подтвердите обычный RDP-вход. Факт запуска приложения — в следующем списке гостевых процессов.";});hypervOnly.Add(button);return button;}
        var kill=B("Завершить выбранный ГОСТЕВОЙ PID",async()=>{
            if(processes.CurrentRow?.Tag is not JsonElement row)throw new InvalidOperationException("Выберите процесс");
            if(!current.HasValue||!Passed(current.Value)||current.Value.GetProperty("vm_id").GetString()!=controller.Load()?.VmId)throw new InvalidOperationException("Обновите список для текущей привязанной VM");
            int pid=row.GetProperty("pid").GetInt32();if(!Confirm("Принудительно завершить только гостевой "+row.GetProperty("name").GetString()+" PID "+pid+"? Несохранённые данные могут потеряться. Host PID не затрагиваются."))return;
            Show(await controller.Run("StopProcess",pid,row.GetProperty("start_ticks").GetInt64(),row.GetProperty("session_id").GetInt32()));await RefreshBackend();
        });
        hypervOnly.Add(kill);stack.Add(Ui.Row(Launch("edge"),Launch("chrome"),Launch("telegram"),kill));
        foreach(var name in new[]{"Guest PID","Приложение","Session","Working set MiB","Private commit MiB"})processes.Columns.Add(name,name);
        stack.Add(processes).Add(status).Add(output,true);Controls.Add(stack);
        try{ReloadProfiles();}catch{status.Text="Каталог профилей не прочитан/невалиден; существующие VM не меняются.";}
        poll.Tick+=async(_,_)=>{if(!busy&&saved?.BackendKind==VmBackendKind.HyperV&&Enabled&&!otherWork())await Perform(RefreshBackend,false);};poll.Start();
        Disposed+=(_,_)=>{poll.Stop();poll.Dispose();};
    }
    void ReloadProfiles()
    {
        var c=controller.Catalog();controller.PinProfile(c.ActiveId);saved=c.Active;loadingProfiles=true;
        try{profiles.Items.Clear();foreach(var e in c.Entries){int i=profiles.Items.Add(new ProfileChoice(e.Id,e.Profile.VmName+" · тип"+(int)e.Profile.BackendKind));if(e.Id==c.ActiveId)profiles.SelectedIndex=i;}}
        finally{loadingProfiles=false;}
        if(saved!=null){vm.Text=saved.VmName;host.Text=saved.RdpHost;collection.Text=saved.CollectionName;min.Value=saved.MinimumMiB;startup.Value=saved.StartupMiB;max.Value=saved.MaximumMiB;buffer.Value=saved.MemoryBufferPercent;auto.Checked=saved.AutoStartVm;sysCapacity.Value=saved.SysStoreCapacity;sysQuota.Value=saved.SysStoreQuota/(1024*1024);}
        baseline=null;current=null;processes.Rows.Clear();ApplyKindControls();status.Text=saved==null?"Создайте профиль: тип1 наш SYS, тип2 Hyper-V.":$"{saved.VmName} · тип{(int)saved.BackendKind}. "+(saved.BackendKind==VmBackendKind.Sys?"SYS store без Hyper-V; запуск Windows-VM пока недоступен.":"Hyper-V; это НЕ наш SYS pager.");
    }
    void ApplyKindControls(){bool hv=saved?.BackendKind==VmBackendKind.HyperV;foreach(var c in hypervOnly)c.Enabled=!busy&&hv;foreach(var c in sysOnly)c.Enabled=!busy&&saved?.BackendKind==VmBackendKind.Sys;}
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
        finally{work=null;busy=false;if(!IsDisposed){foreach(var c in controls)c.Enabled=true;ApplyKindControls();}}
    }
    public async Task AgentStarted()
    {
        if(saved!=null&&saved.AutoStartVm)await Perform(async()=>Show(await controller.Run("StartVm")),false);
        if(saved!=null)await Perform(RefreshBackend,false);
    }
    public async Task WaitAndStop(){poll.Stop();if(work!=null)await work;}
}
