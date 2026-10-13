using System.Diagnostics;
using System.Text.Json;
using ElasticMem.Core.Research;
namespace ElasticMem.App.Research;
internal sealed class ResearchTab:TabPage
{
    sealed class ModeComboBox:ComboBox
    {
        public ModeComboBox(){DropDownStyle=ComboBoxStyle.DropDownList;AutoSize=true;DropDownWidth=850;}
        public override Size GetPreferredSize(Size proposedSize){int w=Math.Max(420,Items.Cast<object>().Select(x=>TextRenderer.MeasureText(x.ToString(),Font).Width+48).DefaultIfEmpty(420).Max());return new Size(Math.Min(w,MaximumSize.Width>0?MaximumSize.Width:w),base.GetPreferredSize(proposedSize).Height);}
    }
    readonly ComboBox _mode=new ModeComboBox();
    readonly TextBox _pids=new(){Width=200,PlaceholderText="PID через запятую"};
    readonly NumericUpDown _seconds=N(1,3600,120),_limit=N(1,64,16),_records=N(1000,5000000,100000),_iterations=N(10,2000,100),_pool=N(16,128,32),_adapter=N(0,15,0);
    readonly NumericUpDown _eviction=N(1,1000000,10),_recovery=N(1,1000000,100),_maxFault=N(0,1000000,100);
    readonly TextBox _trace=new(){Width=320,PlaceholderText="Полная event-трасса CSV"},_metadata=new(){Width=320,PlaceholderText="Metadata JSON"},_scratch=new(){Width=320};
    readonly CheckBox _extended=new(){Text="Расширенный Г0/Г5: 10 минут на рендерер (до 55 минут всего)",AutoSize=true,Checked=true};
    readonly CheckBox _allowTrim=new(){Text="Г0: разрешаю trim ВСЕГО рабочего набора рендереров каждую секунду (возможны задержки)",AutoSize=true};
    readonly TextBox _source=new(){Width=420,PlaceholderText="Папка успешного Г1 со snapshots"};
    readonly CheckBox _negative=new(){Text="Проверить пропуск DMA и неверный offset",AutoSize=true};
    readonly Button _run=new(){Text="Запустить проверку",AutoSize=true},_cancel=new(){Text="Отменить",AutoSize=true,Enabled=false};
    readonly Label _hint=Ui.Hint(""),_status=Ui.Hint("Готов к проверкам. Ничего не включается автоматически.");
    readonly TextBox _output=new(){Multiline=true,ReadOnly=true,WordWrap=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,Font=new Font("Consolas",10)};
    readonly Dictionary<string,Control> _options=new();readonly List<Control> _config=new();
    readonly Func<bool> _agentRunning;readonly Func<Task>? _prepareAuto;
    readonly Button _auto=new(){Text="Проверить всё — автоматически",AutoSize=true};
    AutoOutcome? _batch;
    CancellationTokenSource? _cts;string? _last;
    TaskCompletionSource? _completion;Task? _auditTask;
    public bool IsRunning=>_cts!=null||_auditTask!=null;
    public event Action<bool>? BusyChanged;
    public async Task CancelAndWait(){_cts?.Cancel();if(_completion!=null)await _completion.Task;if(_auditTask!=null)await _auditTask;}
    public void CancelRun()=>_cts?.Cancel();
    static NumericUpDown N(decimal min,decimal max,decimal value)=>new(){Minimum=min,Maximum=max,Value=value,Width=max>100000?120:88,ThousandsSeparator=true};
    static readonly (string Key,string Title,string Hint)[] Modes={
        ("environment","Windows · окружение и MMAgent","Read-only проверка памяти, Memory Compression и PageCombining. Ничего не включает и не меняет. Статус combining не равен измеренной экономии."),
        ("selftest","Самопроверка встроенных механизмов","Детерминированные проверки хранилища, нормализации, модели и хешей. GPU/Windows API проверяются отдельно."),
        ("g0-sample","Г0 · наблюдение процессов Windows","Счётчики рабочего набора, private bytes, CPU и page faults. Это выборочные наблюдения, НЕ полная трасса обращений и НЕ потолок экономии."),
        ("g0-model","Г0 · модель по полной трассе","Импорт exact/synthetic событий с map/read/write/unmap. Метрика — средние байты модели gap−e−r, не верхняя граница любого механизма. Полный сборщик трассы пока внешний."),
        ("g0-epochs","Г0 · эпохи с вмешательством","Требует отдельного согласия EmptyWorkingSet. Базовые счётчики/trim/trim+опрос; приближённая модель повторного появления в WS, НЕ точная трасса/Belady. Не отключает системное сжатие или pagefile."),
        ("epochs-replay","Г0/Г5 · локальный пересчёт карт","Папка успешного Г0-epochs или auto-summary; пусто — последняя локальная партия. Без чтения процессов/trim. Длинные пороги, всплески и каузальные блоки1/4/8/16. Старый ZIP без EET недостаточен."),
        ("g5","Г5 · упаковка по совместному доступу","Вход — локальная папка успешного Г0-epochs 0.17+. Карты/снимки остаются локально. Train-prefix/test-suffix, соседство/coaccess/content; не live-пейджер."),
        ("g4-churn","Г4 · собственный churn-контроль","Синтетические alloc/free/write собственных 64 страниц. Восстановление живых данных, dead slots и перепаковка; не churn чужого процесса."),
        ("disk-qd1","Диск · QD1 без GPU","Свой временный файл 32 МиБ, unbuffered чтение 4/64/256 КиБ, проверки generation/offset. НЕ задержка pagefile и НЕ подтверждение NVMe."),
        ("g0-calibration","Г0 · калибровка собственного процесса","10 раундов baseline/trim/trim+query, описательный bootstrap-интервал. Timed post-trim reads: источник неизвестен, не Xpress/pagefile latency."),
        ("g4","Г4 · Xpress / LZ4 / zstd","Вход — папка успешного Г1. Обучение словаря на одном PID, тест на других. Размеры с метаданными/резервом и проверка восстановления, не реальное освобождение RAM."),
        ("g3-dma","Г3 · DMA без GPU_UPLOAD","DEFAULT→READBACK через copy queue. 4/64/256 КиБ, restore/roundtrip/fence, отрицательные проверки. Подходит для попытки на RX570; не pagefile и не постоянная VRAM-резидентность."),
        ("g1","Г1 · резидентные private-страницы","Выберите 1–4 процесса одного семейства. Основной вход — страницы, прошедшие проверки resident MEM_PRIVATE, not shareable/shared до и после чтения. Invalid/image/mapped/shareable страницы по предварительным проверкам пропускаются; гонка всё ещё может вызвать page fault. Есть Brotli без дедупликации. Порядковая нормализация — закрытый контрольный вариант."),
        ("g2","Г2 · типизированное хранилище","Рабочее компактное хранилище собственных записей со словарём типов, индексными ссылками и escape-счётчиками. Не патч CPython/V8 и не сжатие чужих куч."),
        ("gpu-probe","Г3 · проверить GPU_UPLOAD","Проверяет адаптеры D3D12, дискретность, GPU_UPLOAD и бюджет VRAM. Не запускает нагрузку и не измеряет скорость."),
        ("g3","Г3 · VRAM / диск: микротест","Запись своих данных в GPU_UPLOAD, DMA READBACK и восстановление в RAM; сравнение с unbuffered file I/O. Нужна поддержка GPU_UPLOAD. Это НЕ перенос pagefile в VRAM. Выберите папку на SSD: временный файл создаётся с новым именем и удаляется."),
    };
    public ResearchTab(Func<bool> agentRunning,Func<Task>? prepareAuto=null):base("Лаборатория")
    {
        _agentRunning=agentRunning;_prepareAuto=prepareAuto;
        _pids.Text="";_scratch.Text=Path.GetTempPath();_mode.Items.AddRange(Modes.Select(x=>x.Title).Cast<object>().ToArray());
        var refresh=new Button{Text="Выбрать процессы",AutoSize=true};refresh.Click+=(_,_)=>ChoosePids();
        var chooseTrace=new Button{Text="CSV…",AutoSize=true};chooseTrace.Click+=(_,_)=>Pick(_trace,"CSV|*.csv");
        var chooseMeta=new Button{Text="JSON…",AutoSize=true};chooseMeta.Click+=(_,_)=>Pick(_metadata,"JSON|*.json");
        var chooseDisk=new Button{Text="Папка SSD…",AutoSize=true};chooseDisk.Click+=(_,_)=>{using var d=new FolderBrowserDialog();if(d.ShowDialog(this)==DialogResult.OK)_scratch.Text=d.SelectedPath;};
        var stack=new VStack{Padding=new Padding(16)};
        var help=new Button{Text="Полная инструкция",AutoSize=true};help.Click+=(_,_)=>ResearchHelp.Show(FindForm()!);
        _source.PlaceholderText="Папка Г1 для Г0/Г4; папка Г0-epochs для Г5";
        _auto.Click+=async(_,_)=>await StartAutomatic();
        var replay=new Button{Text="Пересчитать последнюю партию (без trim)",AutoSize=true};replay.Click+=async(_,_)=>{if(IsRunning)return;_source.Text="";_mode.SelectedIndex=Array.FindIndex(Modes,m=>m.Key=="epochs-replay");await Run();};_config.Add(replay);
        stack.Add(Ui.Row(replay));
        stack.Add(Ui.Row(_auto,help)).Add(Ui.Hint("Нажатие «Проверить всё» — согласие на локальное чтение до 128 МиБ суммарно: до 64 МиБ исходных и до 64 МиБ свежих снимков выбранных рендереров Chrome/Edge/Steam. Агент остановится; ZIP без дампов создаётся автоматически. Отправки по сети нет."));
        stack.Add(Ui.Row(_allowTrim)).Add(Ui.Row(_extended));_config.Add(_allowTrim);_config.Add(_extended);
        stack.Add(Ui.Hint("Расширенный режим: наблюдение 10 минут; с trim — ещё ≥10 минут на каждый из 2–4 рендереров. Cap ограничивает опрос, НЕ действие trim. Настройки pagefile/MemoryCompression не меняются."));
        AddOption("source",Ui.Row(_source),stack);
        stack.Add(Ui.Row(Ui.L("Проверка"),_mode,_run,_cancel)).Add(_hint);
        AddOption("pids",Ui.Row(Ui.L("Процессы"),_pids,refresh),stack);AddOption("seconds",Ui.Row(Ui.L("Длительность, с"),_seconds),stack);
        AddOption("limit",Ui.Row(Ui.L("Максимум на процесс, МиБ"),_limit,Ui.L("Всего ≤128 МиБ; античит/защищённые процессы не трогать")),stack);
        AddOption("trace",Ui.Row(_trace,chooseTrace,_metadata,chooseMeta),stack);
        AddOption("costs",Ui.Row(Ui.L("Eviction, мкс"),_eviction,Ui.L("Recovery, мкс"),_recovery,Ui.L("Макс. промах, мкс"),_maxFault),stack);
        AddOption("records",Ui.Row(Ui.L("Собственных записей"),_records),stack);
        AddOption("gpu",Ui.Row(Ui.L("Адаптер"),_adapter,Ui.L("Повторов"),_iterations,Ui.L("Пул VRAM, МиБ"),_pool,_negative),stack);
        AddOption("disk",Ui.Row(_scratch,chooseDisk),stack);
        var open=new Button{Text="Последний прогон",AutoSize=true};open.Click+=(_,_)=>Open(_last??ResearchJobs.Root);
        var export=new Button{Text="Экспорт логов ZIP",AutoSize=true};export.Click+=(_,_)=>Export();
        var uiAudit=new Button{Text="Проверка интерфейса",AutoSize=true};uiAudit.Click+=async(_,_)=>{if(IsRunning)return;_run.Enabled=false;_auto.Enabled=false;try{_auditTask=UiDiagnostics.Capture(FindForm()!);await _auditTask;}finally{_auditTask=null;_run.Enabled=true;_auto.Enabled=true;}_output.Text="Снимки двух тем и layout-audit сохранены. Включите чувствительные файлы при экспорте, чтобы приложить screenshots.";};
        var verify=new Button{Text="Проверить хеши",AutoSize=true};verify.Click+=(_,_)=>{try{if(_last==null)throw new InvalidOperationException("Сначала выполните прогон");ResearchRun.Verify(_last);_status.Text="Хеши совпали. Это целостность, не доказательство эффективности.";}catch(Exception ex){_status.Text=ex.Message;}};
        stack.Add(Ui.Row(open,verify,export,uiAudit)).Add(_status).Add(_output,true);Controls.Add(stack);
        _mode.SelectedIndexChanged+=(_,_)=>UpdateMode();_mode.SelectedIndex=0;
        _run.Click+=async(_,_)=>await Run();_cancel.Click+=(_,_)=>_cts?.Cancel();
    }
    void AddOption(string key,Control row,VStack stack){_options[key]=row;_config.Add(row);stack.Add(row);}
    void UpdateMode()
    {
        string key=Modes[_mode.SelectedIndex].Key;_hint.Text=Modes[_mode.SelectedIndex].Hint;
        foreach(var row in _options)row.Value.Visible=row.Key switch{"pids"=>key is "g0-sample" or "g1","seconds"=>key=="g0-sample","limit"=>key=="g1","trace" or "costs"=>key=="g0-model","records"=>key=="g2","gpu"=>key is "g3" or "g3-dma","disk"=>key is "g3" or "disk-qd1","source"=>key is "g4" or "g0-epochs" or "g5" or "epochs-replay",_=>false};
        Parent?.PerformLayout();Controls[0].PerformLayout();
    }
    internal ResearchOptions Options()=>new(_pids.Text.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(int.Parse).Distinct().ToArray(),(int)_seconds.Value,(int)_limit.Value,(int)_records.Value,(int)_iterations.Value,(int)_pool.Value,(int)_adapter.Value,_scratch.Text,_negative.Checked,_trace.Text,_metadata.Text,(double)_eviction.Value,(double)_recovery.Value,(double)_maxFault.Value,SourceRun:_source.Text,AllowTrim:_allowTrim.Checked,Epochs:_extended.Checked?600:12,Extended:_extended.Checked);
    async Task Run()
    {
        if(IsRunning)return;
        if(_agentRunning()){MessageBox.Show(this,"Остановите агент перед исследованием: trimming меняет измеряемую нагрузку.");return;}
        ResearchOptions options;
        try{options=Options();}catch(Exception ex){_status.Text="Параметры: "+ex.Message;return;}
        string mode=Modes[_mode.SelectedIndex].Key;
        if(mode=="g0-epochs"&&!options.AllowTrim){_status.Text="Нужно отдельное согласие на EmptyWorkingSet.";return;}
        if(mode=="g1"&&options.Pids.Contains(Environment.ProcessId)){_status.Text="Г1: нельзя снимать само приложение ElasticMem — это загрязняет измерение. Выберите внешние процессы.";return;}
        if(mode=="g0-sample"&&options.Pids.Length==0){_status.Text="Выберите процессы для наблюдения.";return;}
        if(mode=="g1"&&(options.Pids.Length is <1 or >4||options.Pids.Length*options.SnapshotMiB>128)){_status.Text="Г1: выберите только 1–4 процесса; всего не более 128 МиБ. Тест не начат.";MessageBox.Show(this,_status.Text,"Параметры Г1",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
        if(options.Pids.Any(pid=>pid<=4)&&mode is "g1" or "g0-sample"){_status.Text="Выберите обычные приложения, не Idle/System (PID 0/4).";return;}
        if(mode=="g1"&&MessageBox.Show(this,"Снимки могут содержать пароли, переписку и другие личные данные. Они остаются локально; по умолчанию не попадают в ZIP логов. Не выбирайте игры с античитом. В Г1 — максимум 4 процесса.\n\nПродолжить чтение памяти выбранных PID?","Г1: явное согласие",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)!=DialogResult.Yes)return;
        _batch=null;_cts=new();_completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);BusyChanged?.Invoke(true);_run.Enabled=false;_auto.Enabled=false;_cancel.Enabled=true;_mode.Enabled=false;foreach(var c in _config)c.Enabled=false;
        try{var progress=new Progress<string>(s=>{if(!IsDisposed)_status.Text=s;});
            string? replayBatch=mode=="epochs-replay"?Path.Combine(AppSettings.Dir,"research-batches",DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")+"-replay"):null;
            _last=await Task.Run(()=>ResearchJobs.Execute(mode,options,progress,_cts.Token,replayBatch==null?null:Path.Combine(replayBatch,"runs")));
            if(replayBatch!=null){string zip=Path.Combine(replayBatch,"ElasticMem-logs-"+Path.GetFileName(replayBatch)+".zip");ResearchRun.ExportLogs(Path.Combine(replayBatch,"runs"),zip,false);Open(replayBatch);}
            _output.Text=File.ReadAllText(Path.Combine(_last,File.Exists(Path.Combine(_last,"result.json"))?"result.json":"report.md"));_status.Text="Сохранено: "+Path.GetFileName(_last);}
        catch(Exception ex){_output.Text=ex.ToString();}
        finally{_cts.Dispose();_cts=null;BusyChanged?.Invoke(false);_completion?.TrySetResult();_run.Enabled=true;_auto.Enabled=true;_cancel.Enabled=false;_mode.Enabled=true;foreach(var c in _config)c.Enabled=true;}
    }
    public async Task StartAutomatic()
    {
        if(IsRunning)return;
        _cts=new();_completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);BusyChanged?.Invoke(true);_auto.Enabled=false;_run.Enabled=false;_cancel.Enabled=true;_mode.Enabled=false;foreach(var c in _config)c.Enabled=false;
        try
        {
            _output.Text="Подготовка автопроверки. Агент будет остановлен; данные остаются локально.";
            if(_prepareAuto!=null)await _prepareAuto();
            if(_agentRunning())throw new InvalidOperationException("Другой бенчмарк ещё работает. Дождитесь его завершения и повторите автопроверку.");
            _cts.Token.ThrowIfCancellationRequested();
            var progress=new Progress<string>(s=>{if(!IsDisposed)_status.Text=s;});
            _batch=await AutoResearch.RunAsync(true,120,progress,_cts.Token,(root,ct)=>UiDiagnostics.Capture(FindForm()!,root,false,ct),_allowTrim.Checked,_extended.Checked,_scratch.Text);
            _last=_batch.LastRun;_output.Text=_batch.Report;_status.Text=string.IsNullOrEmpty(_batch.Archive)?"Автопрогон завершён, ZIP не создан — смотрите причину.":"Готово: "+_batch.Status+" · ZIP собран автоматически";
            if(!string.IsNullOrEmpty(_batch.Archive))Open(Path.GetDirectoryName(_batch.Archive)!);
        }
        catch(OperationCanceledException){_status.Text="Отменено до запуска проверок.";}
        catch(Exception ex){_output.Text=ex.ToString();_status.Text="Автопроверка не завершена.";}
        finally{_cts.Dispose();_cts=null;BusyChanged?.Invoke(false);_completion?.TrySetResult();_auto.Enabled=true;_run.Enabled=true;_cancel.Enabled=false;_mode.Enabled=true;foreach(var c in _config)c.Enabled=true;}
    }
    void Export()
    {
        if(_cts!=null){MessageBox.Show(this,"Дождитесь завершения или отмените прогон.");return;}
        using var d=new SaveFileDialog{Filter="ZIP архив|*.zip",FileName="ElasticMem-logs-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".zip",OverwritePrompt=true};if(d.ShowDialog(this)!=DialogResult.OK)return;
        var choice=MessageBox.Show(this,"Добавить дампы, входные трассы и screenshots? Они могут содержать личные данные.\n\nДа — полный пакет. Нет — только отчёты/логи/счётчики.\nПеред отправкой просмотрите архив.","Приватность экспорта",MessageBoxButtons.YesNoCancel,MessageBoxIcon.Warning);if(choice==DialogResult.Cancel)return;
        try{ResearchRun.ExportLogs(_batch?.RunsRoot??ResearchJobs.Root,d.FileName,choice==DialogResult.Yes);_status.Text="ZIP сохранён локально. Отправьте его вручную; автоматической загрузки нет.";Open(Path.GetDirectoryName(d.FileName)!);}catch(Exception ex){_status.Text=ex.Message;}
    }
    void ChoosePids(){using var dialog=new Form{Text="Выберите PID (не выбирайте игры с античитом)",Width=640,Height=520,StartPosition=FormStartPosition.CenterParent};var list=new ListView{Dock=DockStyle.Fill,View=View.Details,CheckBoxes=true,FullRowSelect=true};list.Columns.Add("Процесс",280);list.Columns.Add("PID",90);list.Columns.Add("Рабочий набор, МиБ",170);foreach(var p in Process.GetProcesses()){using(p){try{list.Items.Add(new ListViewItem(new[]{p.ProcessName,p.Id.ToString(),(p.WorkingSet64>>20).ToString()}){Tag=p.Id});}catch{}}}var ok=new Button{Text="Выбрать",Dock=DockStyle.Bottom,Height=44,DialogResult=DialogResult.OK};dialog.Controls.Add(list);dialog.Controls.Add(ok);Theme.Apply(dialog,Theme.Dark);if(dialog.ShowDialog(this)==DialogResult.OK){var selected=list.CheckedItems.Cast<ListViewItem>().Select(i=>(int)i.Tag!).ToArray();if(Modes[_mode.SelectedIndex].Key=="g1"&&(selected.Length is <1 or >4)){MessageBox.Show(this,"Для Г1 отметьте 1–4 процесса одного приложения. Старый список не изменён.");return;}_pids.Text=string.Join(",",selected);}}
    void Pick(TextBox box,string filter){using var d=new OpenFileDialog{Filter=filter};if(d.ShowDialog(this)==DialogResult.OK)box.Text=d.FileName;}
    static void Open(string folder){Directory.CreateDirectory(folder);Process.Start(new ProcessStartInfo("explorer.exe",folder){UseShellExecute=true});}
}
