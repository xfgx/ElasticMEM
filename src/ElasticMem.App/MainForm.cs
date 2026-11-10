using System.Diagnostics;
using System.Security.Principal;
using ElasticMem.Core;

namespace ElasticMem.App;

public sealed class MainForm : Form
{
    private readonly IdleTrimAgent _agent = new();
    private readonly StartupOptions _startup;
    private readonly System.Windows.Forms.Timer _ui = new() { Interval = 1000 };
    private readonly NotifyIcon _tray = new();
    private readonly string _logPath;

    // Фоновый агент
    private readonly RadioButton _rbAll = new() { Text = "Все приложения", Checked = true, AutoSize = true };
    private readonly RadioButton _rbOne = new() { Text = "Одно приложение:", AutoSize = true };
    private readonly ComboBox _cbProc = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly NumericUpDown _nIdle = new() { Minimum = 5, Maximum = 3600, Value = 30, Width = 70 };
    private readonly NumericUpDown _nMaxIdle = new() { Minimum = 30, Maximum = 86400, Value = 900, Width = 80 };
    private readonly NumericUpDown _nCpu = new() { Minimum = 0, Maximum = 50, DecimalPlaces = 1, Increment = 0.5m, Value = 0.5m, Width = 60 };
    private readonly NumericUpDown _nMinWs = new() { Minimum = 1, Maximum = 10000, Value = 30, Width = 70 };
    private readonly CheckBox _chkFg = new() { Text = "Не трогать активное окно", Checked = true, AutoSize = true };
    private readonly CheckBox _chkSession = new() { Text = "Только мой сеанс", Checked = true, AutoSize = true };
    private readonly CheckBox _chkAlways = new() { Text = "Сжимать всегда (фон)", Checked = false, AutoSize = true };
    private readonly CheckBox _chkAlwaysFg = new() { Text = "…и активное (эксперимент)", Checked = false, AutoSize = true };
    private readonly CheckBox _chkGame = new() { Text = "Игровой режим", Checked = true, AutoSize = true };
    private readonly CheckBox _chkSoft = new() { Text = "Мягкое сжатие (ступенями)", Checked = true, AutoSize = true };
    private readonly CheckBox _chkPrio = new() { Text = "Низкий приоритет памяти фону", Checked = true, AutoSize = true };
    private readonly CheckBox _chkNoGames = new() { Text = "Не трогать игры", Checked = true, AutoSize = true };
    private readonly Button _btnStart = new() { Text = "▶ Запустить агент", Width = 160, Height = 30 };
    private readonly ListView _lv = new() { View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill };
    private readonly Label _lblSys = new() { AutoSize = true, Padding = new Padding(4) };
    private readonly Button _btnMmAgent = new() { Text = "Проверить сжатие памяти Windows", AutoSize = true };

    // Проверка → Анализ
    private readonly ComboBox _cbAnPid = new() { Width = 330, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _cbBlock = new() { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _nLimit = new() { Minimum = 16, Maximum = 8192, Value = 256, Width = 80 };
    private readonly NumericUpDown _nRecheck = new() { Minimum = 0, Maximum = 600, Value = 10, Width = 60 };
    private readonly Button _btnAnalyze = new() { Text = "Анализировать", Width = 130 };
    private readonly Label _lblAn = new() { AutoSize = true };
    private readonly TextBox _txtAn = MakeMono();
    private CancellationTokenSource? _anCts;

    // Проверка → Бенчмарк
    private readonly ComboBox _cbBenchBlock = new() { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _nDur = new() { Minimum = 5, Maximum = 600, Value = 30, Width = 60 };
    private readonly Button _btnBench = new() { Text = "Запустить бенчмарк", Width = 160 };
    private readonly TextBox _txtBench = MakeMono();

    private readonly TextBox _txtLog = MakeMono();
    private TabControl _tabs = null!;
    private AppsTab _appsTab = null!;
    private TabPage _agentTab = null!;
    private UpdateTab _updateTab = null!;
    private AppProfile? _agentProfile;
    private bool _benchBusy;

    private void StartAgentForProfile(AppProfile p)
    {
        if (_agent.Running) ToggleAgent();
        _rbOne.Checked = true;
        _cbProc.Text = string.Join(", ", p.ProcessNames);
        _agentProfile = p;
        ToggleAgent();
        _tabs.SelectedTab = _agentTab;
    }

    public MainForm(StartupOptions startup)
    {
        _startup = startup;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElasticMem");
        Directory.CreateDirectory(dir);
        _logPath = Path.Combine(dir, "agent.log");

        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        Text = $"ElasticMem {Updater.Current}" + (admin ? " [администратор]" : " [без прав администратора]");
        Width = 1200; Height = 800; MinimumSize = new Size(880, 620); StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10f);
        Icon = SystemIcons.Application;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs = tabs;
        tabs.TabPages.Add(new RamSaverTab(MakeMono()));
        _appsTab = new AppsTab(MakeMono(), StartAgentForProfile, Path.Combine(dir, "reports"), () => _agent.Running);
        tabs.TabPages.Add(_appsTab);
        _agentTab = BuildAgentTab();
        tabs.TabPages.Add(_agentTab);
        tabs.TabPages.Add(BuildCheckTab());
        tabs.TabPages.Add(BuildLogTab());
        _updateTab = new UpdateTab(MakeMono(), _startup.RawArgs, AddLog, _startup.UpdatedFrom);
        _updateTab.Notify += m => { try { _tray.ShowBalloonTip(5000, "ElasticMem", m + " — раздел «Обновление».", ToolTipIcon.Info); } catch { } };
        tabs.TabPages.Add(_updateTab);
        tabs.TabPages.Add(BuildAboutTab());

        var settings = AppSettings.Load();
        var chkDark = new CheckBox { Text = "🌙 Тёмная тема", AutoSize = true, Checked = settings.Theme switch { "dark" => true, "light" => false, _ => Theme.SystemPrefersDark() } };
        chkDark.CheckedChanged += (_, _) =>
        {
            var st = AppSettings.Load(); st.Theme = chkDark.Checked ? "dark" : "light"; try { st.Save(); } catch { }
            Theme.Apply(this, chkDark.Checked);
        };
        var shell = new AppShell(tabs, chkDark);
        Controls.Add(shell);
        chkDark.CheckedChanged += (_, _) => shell.RefreshPalette();
        Shown += (_, _) => { var area = Screen.FromControl(this).WorkingArea; MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height)); if (Width > area.Width) Width = area.Width; if (Height > area.Height) Height = area.Height; };
        Theme.Apply(this, chkDark.Checked);

        _tray.Icon = SystemIcons.Application;
        _tray.Text = "ElasticMem";
        _tray.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Остановить агент", null, (_, _) => { if (_agent.Running) ToggleAgent(); });
        menu.Items.Add("Выход", null, (_, _) => { _tray.Visible = false; Application.Exit(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized && _agent.Running) { Hide(); ShowInTaskbar = false; } };

        _agent.Log += AddLog;
        _ui.Tick += (_, _) => RefreshUi();
        int ticks = 0;
        _ui.Tick += (_, _) =>
        {
            if (!_agent.Running) return;
            ticks++;
            if (ticks % 300 == 0) AddLog(_agent.Summary());                 // раз в 5 мин — сводка в журнал
        };
        _ui.Start();
        Load += (_, _) => OnLoaded();
        FormClosing += (_, _) => { _agent.Stop(); _ui.Stop(); _tray.Visible = false; };
    }

    private static TextBox MakeMono() => new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
        Font = new Font("Consolas", 10f), BackColor = Color.White,
    };

    private static FlowLayoutPanel Row(params Control[] cs) => Ui.Row(cs);
    private static Label L(string t) => Ui.L(t);

    private TabPage BuildAgentTab()
    {
        var tp = new TabPage("Фоновый агент");
        foreach (var (h, w) in new[] { ("Процесс", 180), ("PID", 65), ("Роль", 95), ("Рабочий набор, МБ", 125), ("Состояние", 210), ("Доля", 55), ("Простой, с", 85), ("Порог, с", 75), ("Сжатий", 65), ("Передано всего, МБ", 140), ("Возврат, %", 85) })
            _lv.Columns.Add(h, w);
        _btnStart.Click += (_, _) => ToggleAgent();
        _btnMmAgent.Click += async (_, _) => await CheckMmAgent(askEnable: true);
        _rbOne.CheckedChanged += (_, _) => _cbProc.Enabled = _rbOne.Checked;
        _chkAlways.CheckedChanged += (_, _) => { _chkFg.Enabled = !_chkAlways.Checked; _chkAlwaysFg.Enabled = _chkAlways.Checked; };
        _chkFg.Enabled = !_chkAlways.Checked;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { _agent.Stop(); } catch { } };
        _cbProc.Enabled = false;
        var refresh = new Button { Text = "↻", Width = 32 };
        refresh.Click += (_, _) => FillProcessCombos();

        var hint = Ui.Hint("Агент постоянно удерживает рабочий набор выбранных приложений у «горячего» объёма: Windows сама сжимает самые старые страницы. " +
            "Уменьшение рабочего набора не равно экономии физической RAM; возможны рост ошибок страниц и задержки. " +
            "По умолчанию активное окно и игры с античитом исключены. Для одноразовой очистки проще раздел «Освободить память».");
        _lv.Height = 200;
        var st = new VStack();
        st.Add(Row(L("Что сжимать:"), _rbAll, _rbOne, _cbProc, refresh, _btnStart))
          .Add(Row(L("Простой до сжатия, с:"), _nIdle, L("макс. порог, с:"), _nMaxIdle, L("CPU ниже, %:"), _nCpu,
                   L("мин. рабочий набор, МБ:"), _nMinWs))
          .Add(Row(_chkAlways, _chkAlwaysFg, _chkFg, _chkSession, _chkGame, _chkSoft, _chkPrio, _chkNoGames))
          .Add(hint)
          .Add(_lv, fill: true)
          .Add(Row(_lblSys, _btnMmAgent));
        tp.Controls.Add(st);
        return tp;
    }

    private TabPage BuildCheckTab()
    {
        var tp = new TabPage("Проверка");
        var inner = new TabControl { Dock = DockStyle.Fill };
        inner.TabPages.Add(new CompressedHeapTab(MakeMono(), () => _agent.Running || _anCts != null || _benchBusy));
        inner.TabPages.Add(new DedupTab(MakeMono(), () => _agent.Running || _anCts != null || _benchBusy));
        inner.TabPages.Add(new ProofTab(MakeMono(), () => _agent.Running || _anCts != null || _benchBusy));
        inner.TabPages.Add(BuildAnalyzeTab());
        inner.TabPages.Add(BuildBenchTab());
        tp.Controls.Add(inner);
        return tp;
    }

    private TabPage BuildAnalyzeTab()
    {
        var tp = new TabPage("Анализ памяти");
        _cbBlock.Items.AddRange(new object[] { 256, 512, 1024, 4096 }); _cbBlock.SelectedItem = 1024;
        _btnAnalyze.Click += async (_, _) => await RunAnalyze();
        var refresh = new Button { Text = "↻", Width = 32 };
        refresh.Click += (_, _) => FillProcessCombos();
        tp.Controls.Add(new VStack()
            .Add(Ui.Hint("Только чтение памяти выбранного процесса: какие форматы микроблоков подошли бы и насколько это плотнее постраничного сжатия. Ничего не изменяется и никуда не отправляется."))
            .Add(Row(L("Процесс:"), _cbAnPid, refresh, L("Микроблок, Б:"), _cbBlock, L("Лимит, МБ:"), _nLimit,
                     L("Повторный снимок через, с:"), _nRecheck, _btnAnalyze))
            .Add(Row(_lblAn))
            .Add(_txtAn, fill: true));
        return tp;
    }

    private TabPage BuildBenchTab()
    {
        var tp = new TabPage("Бенчмарк");
        _cbBenchBlock.Items.AddRange(new object[] { 256, 512, 1024, 4096 }); _cbBenchBlock.SelectedItem = 1024;
        _btnBench.Click += async (_, _) => await RunBench();
        _txtBench.Text = "Сравнение на тестовых потоках изменений (координаты, счётчики, таблица, разреженные данные, текст, шум, горячий буфер):" + Environment.NewLine +
                         "  1) обычное постраничное сжатие;  2) микроблоки с адаптивными форматами;  3) основа + ограниченный набор патчей;" + Environment.NewLine +
                         "  4) всё вместе + компрессор с прогнозом срока жизни представления." + Environment.NewLine +
                         "Считается вся занятая память (данные, сжатые блоки, патчи, метаданные) и задержки доступа. Каждое чтение сверяется с эталоном.";
        tp.Controls.Add(new VStack()
            .Add(Row(L("Микроблок, Б:"), _cbBenchBlock, L("Длительность (модельные секунды):"), _nDur, _btnBench))
            .Add(_txtBench, fill: true));
        return tp;
    }

    private TabPage BuildLogTab()
    {
        var tp = new TabPage("Журнал");
        tp.Controls.Add(_txtLog);
        return tp;
    }

    private TabPage BuildAboutTab()
    {
        var tp = new TabPage("О программе");
        var t = MakeMono();
        t.WordWrap = true; t.Font = new Font("Segoe UI", 10f);
        t.Text = string.Join(Environment.NewLine, new[]
        {
            "ElasticMem освобождает оперативную память штатными средствами Windows и показывает, насколько ваша память сжимается.",
            "",
            "Разделы:",
            "",
            "• Освободить память — одна кнопка. Холодные страницы приложений вытесняются, Windows Memory Compression их сжимает.",
            "  Для своих приложений права администратора не нужны. Работает на Windows 10 и новее, без драйвера и Hyper-V.",
            "",
            "• Игры и приложения — профили популярных программ и реальный тест: сколько памяти вернулось после сжатия.",
            "",
            "• Фоновый агент — постоянно держит рабочий набор приложений у «горячего» объёма. Windows сама решает, что сжать.",
            "  Агент ничего не сжимает сам и не перехватывает записи; он только подсказывает ОС, какие страницы остыли.",
            "",
            "• Проверка — измерительные инструменты:",
            "    «Сжатая куча» — своя прозрачно-сжатая память в пространстве пользователя (XPRESS); замер степени сжатия и задержки.",
            "    «Анализ памяти» — только чтение процесса: какие форматы микроблоков подошли бы.",
            "    «Бенчмарк» — сравнение стратегий рантайма ElasticStore на модельных потоках изменений.",
            "",
            "Важно: уменьшение рабочего набора не всегда равно экономии физической RAM. Сравнивайте систему с выключенным агентом.",
            "",
            "Запуск из командной строки:",
            "  ElasticMem.exe --agent --all --tray                — фоновый агент для всех приложений, свернуть в трей",
            "  ElasticMem.exe --agent --process chrome --min-idle 60 — только для chrome.exe",
            "  ElasticMem.exe --selftest                          — самопроверка движка",
            "  ElasticMem.exe --scan telegram,chrome,steam        — отчёт по приложениям (только чтение)",
        });
        tp.Controls.Add(t);
        return tp;
    }

    private void OnLoaded()
    {
        FillProcessCombos();
        _ = CheckMmAgent(askEnable: false);
        _ = _updateTab.StartupCheckAsync();
        if (_startup.UpdatedFrom != null && !_startup.StartAgent) _tabs.SelectedTab = _updateTab;
        if (_startup.StartAgent)
        {
            _rbAll.Checked = _startup.All; _rbOne.Checked = !_startup.All;
            _cbProc.Text = _startup.Process;
            _nIdle.Value = (decimal)Math.Clamp(_startup.MinIdle, 5, 3600);
            ToggleAgent();
        }
        if (_startup.Tray && _startup.StartAgent) BeginInvoke(() => { WindowState = FormWindowState.Minimized; Hide(); ShowInTaskbar = false; });
    }

    private void RestoreFromTray() { Show(); ShowInTaskbar = true; WindowState = FormWindowState.Normal; Activate(); }

    private void FillProcessCombos()
    {
        var procs = Process.GetProcesses();
        var byName = procs.GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Name = g.Key, Ws = g.Sum(p => SafeWs(p)), N = g.Count() })
            .OrderByDescending(x => x.Ws).ToList();
        string cur = _cbProc.Text;
        _cbProc.Items.Clear();
        foreach (var x in byName) _cbProc.Items.Add(x.Name);
        _cbProc.Text = cur;

        _cbAnPid.Items.Clear();
        foreach (var p in procs.OrderByDescending(SafeWs))
            _cbAnPid.Items.Add(new PidItem(p.Id, $"{p.ProcessName} (PID {p.Id}) — {SafeWs(p) >> 20} МБ"));
        if (_cbAnPid.Items.Count > 0) _cbAnPid.SelectedIndex = 0;
        foreach (var p in procs) p.Dispose();
    }

    private static long SafeWs(Process p) { try { return p.WorkingSet64; } catch { return 0; } }

    private sealed record PidItem(int Pid, string Label) { public override string ToString() => Label; }

    private void ToggleAgent()
    {
        if (_agent.Running)
        {
            _agent.Stop();
            _btnStart.Text = "▶ Запустить агент";
            return;
        }
        if (_rbOne.Checked && string.IsNullOrWhiteSpace(_cbProc.Text))
        {
            MessageBox.Show(this, "Выберите приложение или укажите имя процесса (например, chrome).", "ElasticMem");
            return;
        }
        _agent.Settings = new AgentSettings
        {
            AllProcesses = _rbAll.Checked, TargetProcessName = _cbProc.Text,
            Profile = _rbOne.Checked && _agentProfile != null && _cbProc.Text == string.Join(", ", _agentProfile.ProcessNames) ? _agentProfile : null,
            MinIdleSec = (double)_nIdle.Value, MaxIdleSec = Math.Max((double)_nMaxIdle.Value, (double)_nIdle.Value),
            CpuThresholdPercent = (double)_nCpu.Value, MinWorkingSetMb = (long)_nMinWs.Value,
            SkipForeground = _chkFg.Checked, CurrentSessionOnly = _chkSession.Checked,
            AlwaysOn = _chkAlways.Checked, AlwaysIncludeActive = _chkAlwaysFg.Checked, GameMode = _chkGame.Checked, SoftTrim = _chkSoft.Checked, LowerMemoryPriority = _chkPrio.Checked, ExcludeGames = _chkNoGames.Checked,
        };
        _agent.Start();
        _btnStart.Text = "■ Остановить агент";
    }

    private void RefreshUi()
    {
        var m = Native.MemoryStatus();
        long store = IdleTrimAgent.CompressionStoreBytes();
        _lblSys.Text = $"ОЗУ: занято {(m.ullTotalPhys - m.ullAvailPhys) >> 20} из {m.ullTotalPhys >> 20} МБ ({m.dwMemoryLoad}%)   " +
                       $"Хранилище сжатой памяти Windows: {(store >= 0 ? (store >> 20) + " МБ" : "н/д")}   " +
                       $"Агент передал на сжатие: {_agent.TotalTrimmed >> 20} МБ";
        _tray.Text = $"ElasticMem — ОЗУ {m.dwMemoryLoad}%{(_agent.Running ? ", агент работает" : "")}";
        if (!_agent.Running || !Visible) return;
        var rows = _agent.Snapshot();
        _lv.BeginUpdate();
        _lv.Items.Clear();
        foreach (var r in rows)
        {
            var it = new ListViewItem(new[]
            {
                r.Name, r.Pid.ToString(), r.Role, (r.WorkingSet >> 20).ToString(), r.State, r.Level, r.IdleSec.ToString("F0"), r.DwellSec.ToString("F0"),
                r.Trims.ToString(), (r.TrimmedTotal >> 20).ToString(), r.LastRefaultPct < 0 ? "—" : r.LastRefaultPct.ToString("F0"),
            });
            if (!r.Access) it.ForeColor = Color.Gray;
            else if (r.State.StartsWith("передан") || r.State.StartsWith("сжат")) it.ForeColor = Color.SeaGreen;
            _lv.Items.Add(it);
        }
        _lv.EndUpdate();
    }

    private void AddLog(string s)
    {
        string line = $"{DateTime.Now:HH:mm:ss}  {s}";
        try { File.AppendAllText(_logPath, line + Environment.NewLine); } catch { }
        if (IsHandleCreated) BeginInvoke(() =>
        {
            if (_txtLog.TextLength > 500_000) _txtLog.Clear();
            _txtLog.AppendText(line + Environment.NewLine);
        });
    }

    private async Task CheckMmAgent(bool askEnable)
    {
        string res = await Task.Run(() => RunPs("(Get-MMAgent).MemoryCompression"));
        bool on = res.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
        _btnMmAgent.Text = on ? "Memory Compression: включено ✓" : "Memory Compression: выключено — включить";
        if (!askEnable || on) return;
        if (MessageBox.Show(this, "Встроенное сжатие памяти Windows выключено. Без него агент только выгружает страницы в файл подкачки.\n\nВключить (Enable-MMAgent -MemoryCompression)? Нужны права администратора, может потребоваться перезагрузка.",
                "ElasticMem", MessageBoxButtons.YesNo) == DialogResult.Yes)
        {
            string r = await Task.Run(() => RunPs("Enable-MMAgent -MemoryCompression; (Get-MMAgent).MemoryCompression"));
            AddLog("Enable-MMAgent: " + r.Trim());
            await CheckMmAgent(false);
        }
    }

    private static string RunPs(string cmd)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{cmd}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return o;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private async Task RunAnalyze()
    {
        if (_anCts != null) { _anCts.Cancel(); return; }
        if (_cbAnPid.SelectedItem is not PidItem item) return;
        int bs = (int)_cbBlock.SelectedItem!;
        long limit = (long)_nLimit.Value << 20;
        double recheck = (double)_nRecheck.Value;
        _anCts = new CancellationTokenSource();
        _btnAnalyze.Text = "Отмена";
        _txtAn.Text = "";
        var prog = new Progress<string>(s => _lblAn.Text = s);
        try
        {
            var token = _anCts.Token;
            string r = await Task.Run(() => ProcessAnalyzer.Analyze(item.Pid, bs, limit, recheck, prog, token));
            _txtAn.Text = r.ReplaceLineEndings(Environment.NewLine);
            _lblAn.Text = "Готово";
        }
        catch (OperationCanceledException) { _lblAn.Text = "Отменено"; }
        catch (Exception ex) { _txtAn.Text = "Ошибка: " + ex.Message; }
        finally { _anCts = null; _btnAnalyze.Text = "Анализировать"; }
    }

    private async Task RunBench()
    {
        if (_benchBusy) return; _benchBusy = true; _btnBench.Enabled = false;
        var cfg = new BenchmarkConfig { BlockSize = (int)_cbBenchBlock.SelectedItem!, DurationSec = (double)_nDur.Value };
        _txtBench.Text = "Выполняется..." + Environment.NewLine;
        try
        {
            var res = await Task.Run(() => Benchmark.Run(cfg, s => BeginInvoke(() => _txtBench.AppendText(s + Environment.NewLine))));
            _txtBench.Text = Benchmark.Format(res).ReplaceLineEndings(Environment.NewLine);
        }
        catch (Exception ex) { _txtBench.AppendText("Ошибка: " + ex); }
        finally { _btnBench.Enabled = true; _benchBusy = false; }
    }
}
