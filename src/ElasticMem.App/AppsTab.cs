using System.Diagnostics;
using ElasticMem.Core.Access;

namespace ElasticMem.App;

/// <summary>Вкладка «Игры и приложения»: готовые профили Dota 2, Steam, Яндекс Музыка, Telegram, Chrome.</summary>
public sealed class AppsTab : TabPage
{
    private readonly ListView _lv = new() { View = View.Details, FullRowSelect = true, CheckBoxes = true, Height = 170, Dock = DockStyle.Top, HideSelection = false };
    private readonly TextBox _custom = new() { Width = 260, PlaceholderText = "свои процессы: name1, name2" };
    private readonly NumericUpDown _nLimit = new() { Minimum = 64, Maximum = 8192, Value = 512, Width = 70 };
    private readonly NumericUpDown _nRecheck = new() { Minimum = 0, Maximum = 600, Value = 10, Width = 55 };
    private readonly CheckBox _chkTrim = new() { Text = "Тест рабочего набора (меняет процесс)", AutoSize = true };
    private readonly NumericUpDown _nObserve = new() { Minimum = 5, Maximum = 600, Value = 30, Width = 55 };
    private readonly Button _btnRun = new() { Text = "▶ Тестировать отмеченные", AutoSize = true };
    private readonly Button _btnAgent = new() { Text = "Агент для выбранного", AutoSize = true };
    private readonly Button _btnFolder = new() { Text = "Папка отчётов", AutoSize = true };
    private readonly Button _btnRefresh = new() { Text = "↻ Обновить", AutoSize = true };
    private readonly CheckBox _chkSend = new() { Text = "Отправлять отчёты разработчику", AutoSize = true };
    private readonly Button _btnSend = new() { Text = "Отправить отчёты и журнал", AutoSize = true };
    private readonly AppSettings _settings = AppSettings.Load();
    private string? _lastAll;
    private readonly Label _status = new() { AutoSize = true, Padding = new Padding(4) };
    private readonly TextBox _out;
    private readonly Action<AppProfile> _startAgent;
    private readonly string _reportDir;
    private CancellationTokenSource? _cts;

    public bool HasActiveWork=>_cts!=null;
    private readonly Func<bool> _agentRunning;
    private readonly Button _btnAlways = new() { Text = "Тест «Сжимать всегда»", AutoSize = true };
    private readonly NumericUpDown _nAlwaysMin = new() { Minimum = 1, Maximum = 240, Value = 10, Width = 55 };
    private readonly Button _btnExp = new() { Text = "⚖ Сравнительный эксперимент A/B/C/D", AutoSize = true };
    private readonly NumericUpDown _nExpMin = new() { Minimum = 2, Maximum = 60, Value = 5, Width = 50 };
    private readonly NumericUpDown _nExpRounds = new() { Minimum = 1, Maximum = 3, Value = 1, Width = 40 };

    public AppsTab(TextBox output, Action<AppProfile> startAgent, string reportDir, Func<bool> agentRunning) : base("Игры и приложения")
    {
        _out = output; _startAgent = startAgent; _reportDir = reportDir; _agentRunning = agentRunning;
        _btnAlways.Click += async (_, _) => await RunAlwaysTest();
        _btnExp.Click += async (_, _) => await RunExperiment();
        Directory.CreateDirectory(reportDir);
        foreach (var (h, w) in new[] { ("Приложение", 210), ("Процессов", 80), ("Рабочий набор, МБ", 130), ("Примечание", 600) }) _lv.Columns.Add(h, w);
        _btnRefresh.Click += (_, _) => RefreshList();
        _btnRun.Click += async (_, _) => await RunChecked();
        _btnAgent.Click += (_, _) => { if (Selected() is { } p) _startAgent(p); };
        _btnFolder.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", _reportDir) { UseShellExecute = true });

        _chkSend.Checked = false; _chkSend.Visible = false; _chkSend.Enabled = false;
        _settings.SendReports = false;
        _chkSend.CheckedChanged += (_, _) => { var st = AppSettings.Load(); st.SendReports = _chkSend.Checked; try { st.Save(); } catch { } };
        _btnSend.Text = "Локальные отчёты";
        _btnSend.Click += (_, _) => { Process.Start(new ProcessStartInfo("explorer.exe", _reportDir) { UseShellExecute=true }); _status.Text="Старые проверки сохраняют TXT в этой папке; журнал агента — %LOCALAPPDATA%\\ElasticMem\\agent.log. Пришлите нужные файлы вручную."; };
        var flow1 = Row(_btnRefresh, L("Свой профиль:"), _custom, _btnRun, _btnAgent, _btnFolder);
        var flow3 = Row(_btnExp, L("мин на фазу:"), _nExpMin, L("кругов:"), _nExpRounds, _btnAlways, L("мин:"), _nAlwaysMin, _chkSend, _btnSend);
        var flow2 = Row(L("Лимит скана, МБ:"), _nLimit, L("Повторный снимок через, с:"), _nRecheck, _chkTrim, L("наблюдать, с:"), _nObserve);
        var hint = Ui.Hint("Запустите нужные приложения, отметьте их галочками и нажмите «Тестировать». Скан только читает память. " +
                   "«Тест рабочего набора» изменяет WS процесса и измеряет возврат; Windows сама выбирает сжатие или выгрузку, возможны задержки. " +
                   "Отчёты сохраняются в папку отчётов остаются локально. Для ручной отправки используйте экспорт ZIP; содержимое памяти по умолчанию исключено.");
        _lv.Height = 190;
        Controls.Add(new VStack()
            .Add(flow1).Add(flow2).Add(flow3).Add(hint).Add(_lv).Add(Row(_status)).Add(_out, fill: true));
        RefreshList();
    }

    private static FlowLayoutPanel Row(params Control[] cs) => Ui.Row(cs);
    private static Label L(string t) => Ui.L(t);

    private AppProfile? CustomProfile()
    {
        var names = _custom.Text.Split(',', ';').Select(x => x.Trim()).Select(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? x[..^4] : x).Where(x => x.Length > 0).ToArray();
        return names.Length == 0 ? null : new AppProfile("Свой: " + string.Join(", ", names), names, Array.Empty<string>(), "Профиль пользователя");
    }

    public void RefreshList()
    {
        _lv.BeginUpdate();
        var checkedTitles = _lv.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => i.Text).ToHashSet();
        _lv.Items.Clear();
        var all = AppProfile.Builtin.ToList();
        if (CustomProfile() is { } cp) all.Add(cp);
        foreach (var p in all)
        {
            var procs = p.Find();
            long ws = 0; foreach (var x in procs) { try { ws += x.WorkingSet64; } catch { } x.Dispose(); }
            var it = new ListViewItem(new[] { p.Title, procs.Count.ToString(), (ws >> 20).ToString(), p.Note }) { Tag = p };
            it.Checked = checkedTitles.Contains(p.Title) || (checkedTitles.Count == 0 && procs.Count > 0 && !p.AntiCheatRisk);
            if (procs.Count == 0) it.ForeColor = Color.Gray;
            if (p.AntiCheatRisk && procs.Count > 0) it.ForeColor = Color.DarkOrange;
            _lv.Items.Add(it);
        }
        _lv.EndUpdate();
        _status.Text = "Запущено профилей: " + _lv.Items.Cast<ListViewItem>().Count(i => i.SubItems[1].Text != "0");
    }

    private AppProfile? Selected() =>
        _lv.SelectedItems.Count > 0 ? (AppProfile)_lv.SelectedItems[0].Tag! :
        _lv.CheckedItems.Count > 0 ? (AppProfile)_lv.CheckedItems[0].Tag! : null;

    private async Task RunChecked()
    {
        if (_cts != null) { _cts.Cancel(); return; }
        RefreshList();
        var profs = _lv.CheckedItems.Cast<ListViewItem>().Select(i => (AppProfile)i.Tag!).ToList();
        if (profs.Count == 0) { MessageBox.Show(this, "Отметьте хотя бы одно запущенное приложение.", "ElasticMem"); return; }
        foreach (var p in profs.Where(p => p.AntiCheatRisk))
            if (MessageBox.Show(this, p.Note + "\n\nПродолжить для " + p.Title + "?", "Античит", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                profs.Remove(p);
        if (profs.Count == 0) return;

        var o = new ProfileScanner.Options
        {
            TotalLimitBytes = (long)_nLimit.Value << 20, PerProcessLimitBytes = Math.Max(64L << 20, ((long)_nLimit.Value << 20) / 2),
            RecheckSec = (double)_nRecheck.Value, RunTrimTest = _chkTrim.Checked, TrimObserveSec = (double)_nObserve.Value,
        };
        _cts = new CancellationTokenSource();
        _btnRun.Text = "■ Отмена";
        _out.Text = "";
        var prog = new Progress<string>(s => _status.Text = s);
        var summary = new List<string>();
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        try
        {
            foreach (var p in profs)
            {
                var token = _cts.Token;
                string rep;
                try { rep = await Task.Run(() => ProfileScanner.Run(p, o, prog, token)); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { rep = $"{p.Title}: ошибка — {ex.Message}"; }
                _out.AppendText(rep.ReplaceLineEndings(Environment.NewLine) + Environment.NewLine);
                summary.Add(rep);
                string file = Path.Combine(_reportDir, $"{stamp}_{Safe(p.Title)}.txt");
                File.WriteAllText(file, rep);
            }
            string all = Path.Combine(_reportDir, $"{stamp}_ВСЕ.txt");
            File.WriteAllText(all, SystemInfo() + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine, summary));
            _status.Text = "Готово. Сводный отчёт: " + all;
            _lastAll = all;
            
        }
        catch (OperationCanceledException) { _status.Text = "Отменено"; }
        finally { _cts = null; _btnRun.Text = "▶ Тестировать отмеченные"; }
    }

    /// <summary>
    /// Проверка постоянного режима на отмеченных приложениях: агент «Сжимать всегда» на N минут,
    /// пользуйтесь приложениями как обычно. Итог: средний/минимальный рабочий набор против исходного, возврат, работает ли мягкий предел.
    /// </summary>
    private async Task RunAlwaysTest()
    {
        if (_cts != null) { _cts.Cancel(); return; }
        if (_agentRunning()) { MessageBox.Show(this, "Сначала остановите основной агент (вкладка «Агент сжатия»).", "ElasticMem"); return; }
        RefreshList();
        var profs = _lv.CheckedItems.Cast<ListViewItem>().Select(i => (AppProfile)i.Tag!).Where(p => !p.AntiCheatRisk).ToList();
        if (profs.Count == 0) { MessageBox.Show(this, "Отметьте запущенные приложения (игры с античитом в этот тест не входят).", "ElasticMem"); return; }
        int minutes = (int)_nAlwaysMin.Value;
        var agent = new IdleTrimAgent
        {
            Settings = new AgentSettings
            {
                AllProcesses = false, TargetProcessName = string.Join(",", profs.SelectMany(p => p.ProcessNames)),
                AlwaysOn = true, AlwaysIncludeActive = true, MinWorkingSetMb = 16, SkipForeground = false,
            },
        };
        var log = new List<string>();
        agent.Log += m => { lock (log) log.Add($"{DateTime.Now:HH:mm:ss} {m}"); };
        long Ws(AppProfile p) { long w = 0; foreach (var x in p.Find()) { try { w += x.WorkingSet64; } catch { } x.Dispose(); } return w; }
        var ws0 = profs.ToDictionary(p => p.Title, Ws);
        var sum = profs.ToDictionary(p => p.Title, _ => 0.0); var mn = profs.ToDictionary(p => p.Title, p => ws0[p.Title]);
        var mx = profs.ToDictionary(p => p.Title, p => ws0[p.Title]);
        int samples = 0;
        long store0 = IdleTrimAgent.CompressionStoreBytes(); var mem0 = Native.MemoryStatus();
        _cts = new CancellationTokenSource(); _btnAlways.Text = "■ Остановить тест"; _out.Text = "";
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        agent.Start();
        try
        {
            var end = DateTime.Now.AddMinutes(minutes);
            while (DateTime.Now < end)
            {
                await Task.Delay(5000, _cts.Token);
                samples++;
                foreach (var p in profs) { long w = Ws(p); sum[p.Title] += w; mn[p.Title] = Math.Min(mn[p.Title], w); mx[p.Title] = Math.Max(mx[p.Title], w); }
                _status.Text = $"«Сжимать всегда»: осталось {(end - DateTime.Now).TotalSeconds:F0} с — пользуйтесь приложениями как обычно. " +
                               string.Join("; ", profs.Select(p => $"{p.Title}: {ws0[p.Title] >> 20} → {(long)(sum[p.Title] / samples) >> 20} МБ"));
            }
        }
        catch (OperationCanceledException) { }
        var rows = agent.Snapshot();
        string summary = agent.Summary();
        agent.Stop();
        long store1 = IdleTrimAgent.CompressionStoreBytes(); var mem1 = Native.MemoryStatus();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"══ Тест «Сжимать всегда» — {DateTime.Now:yyyy-MM-dd HH:mm}, {samples * 5 / 60.0:F1} мин ══");
        foreach (var p in profs)
        {
            double avg = samples > 0 ? sum[p.Title] / samples : ws0[p.Title];
            sb.AppendLine($"  {p.Title,-26} было {ws0[p.Title] >> 20,5} МБ → в среднем {(long)avg >> 20,5} МБ (мин {mn[p.Title] >> 20}, макс {mx[p.Title] >> 20}); " +
                          $"экономия в среднем {(ws0[p.Title] - (long)avg) >> 20} МБ ({(ws0[p.Title] > 0 ? 100.0 * (ws0[p.Title] - avg) / ws0[p.Title] : 0):F0}%)");
        }
        sb.AppendLine($"  Отжато всего {agent.TotalTrimmed >> 20} МБ; процессов без мягкого предела: {rows.Count(r => r.CapUseless)} из {rows.Count}");
        sb.AppendLine($"  Хранилище сжатой памяти: {store0 >> 20} → {store1 >> 20} МБ; свободная RAM: {mem0.ullAvailPhys >> 20} → {mem1.ullAvailPhys >> 20} МБ");
        sb.AppendLine("  " + summary);
        sb.AppendLine("  Процессы (цель / рабочий набор / возврат):");
        foreach (var r in rows.Take(25))
            sb.AppendLine($"    {r.Name,-16} {r.Pid,-6} {r.Role,-12} цель {r.Target >> 20,5} МБ, РН {r.WorkingSet >> 20,5} МБ, пик {r.Peak >> 20,5} МБ, возврат {r.RateMbps,4:F1} МБ/с{(r.CapUseless ? ", БЕЗ мягкого предела" : "")}{(r.Protected ? ", защищён" : "")}");
        sb.AppendLine("  Журнал:");
        lock (log) foreach (var l in log.TakeLast(150)) sb.AppendLine("    " + l);
        string rep = sb.ToString();
        _out.Text = rep.ReplaceLineEndings(Environment.NewLine);
        string file = Path.Combine(_reportDir, $"{stamp}_ВСЕГДА_ВСЕ.txt");
        File.WriteAllText(file, SystemInfo() + Environment.NewLine + rep);
        _lastAll = file;
        _status.Text = "Тест завершён: " + file;
        _cts = null; _btnAlways.Text = "Тест «Сжимать всегда»";
        
    }

    private async Task RunExperiment()
    {
        if (_cts != null) { _cts.Cancel(); return; }
        if (_agentRunning()) { MessageBox.Show(this, "Сначала остановите основной агент (вкладка «Агент сжатия»).", "ElasticMem"); return; }
        RefreshList();
        var profs = _lv.CheckedItems.Cast<ListViewItem>().Select(i => (AppProfile)i.Tag!).Where(p => !p.AntiCheatRisk).ToList();
        if (profs.Count == 0) { MessageBox.Show(this, "Отметьте запущенные приложения.", "ElasticMem"); return; }
        double min = (double)_nExpMin.Value; int rounds = (int)_nExpRounds.Value;
        var order = new[] { Experiment.Phase.A, Experiment.Phase.B, Experiment.Phase.C, Experiment.Phase.D };
        if (MessageBox.Show(this, $"Будет {rounds * 4} фаз по {min:F0} мин (≈{rounds * 4 * min:F0} мин).\n\n{Experiment.Scenario}\n\nПеред каждой фазой появится окно — нажмите ОК и выполните сценарий.",
                "Сравнительный эксперимент", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        _cts = new CancellationTokenSource(); _btnExp.Text = "■ Остановить эксперимент"; _out.Text = Experiment.Scenario.ReplaceLineEndings(Environment.NewLine) + Environment.NewLine;
        var all = new List<Experiment.Stats>(); var log = new List<string>();
        var prog = new Progress<string>(s => _status.Text = s);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        try
        {
            for (int r = 1; r <= rounds; r++)
                foreach (var ph in order)
                {
                    MessageBox.Show(this, $"Фаза {Experiment.Name(ph)} (круг {r}/{rounds}), {min:F0} мин.\nНажмите ОК и выполните сценарий.", "Эксперимент");
                    var token = _cts.Token;
                    var st = await Task.Run(() => Experiment.RunPhase(ph, r, profs, min, prog, token, log));
                    all.Add(st);
                    _out.AppendText($"Готово: {Experiment.Name(ph)}, круг {r}: занято в среднем {st.InUse / (1 << 20):F0} МБ, p95 отклика {st.UiP95:F1} мс{Environment.NewLine}");
                }
        }
        catch (OperationCanceledException) { _status.Text = "Эксперимент остановлен — отчёт по завершённым фазам"; }
        finally { _cts = null; _btnExp.Text = "⚖ Сравнительный эксперимент A/B/C/D"; }
        if (all.Count == 0) return;
        string rep = Experiment.Format(all, profs, log);
        _out.Text = rep.ReplaceLineEndings(Environment.NewLine);
        string file = Path.Combine(_reportDir, $"{stamp}_ЭКСПЕРИМЕНТ_ВСЕ.txt");
        File.WriteAllText(file, SystemInfo() + Environment.NewLine + rep);
        _lastAll = file;
        _status.Text = "Эксперимент завершён: " + file;
        
    }

    public static string SystemInfo()
    {
        var m = Native.MemoryStatus();
        return $"ElasticMem {typeof(AppsTab).Assembly.GetName().Version} | {Environment.OSVersion} | ЦП: {Environment.ProcessorCount} потоков | " +
               $"ОЗУ {m.ullTotalPhys >> 20} МБ, занято {m.dwMemoryLoad}% | Сжатая память Windows: {IdleTrimAgent.CompressionStoreBytes() >> 20} МБ | ID {ReportUploader.MachineId}";
    }

    private static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
}
