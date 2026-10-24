using System.Diagnostics;
using System.Text;
using ElasticMem.Core;

namespace ElasticMem.App;

/// <summary>
/// «Сжатие в реальном времени» — предиктивное формирование рабочего набора. Держит горячие регионы процесса
/// в памяти (PrefetchVirtualMemory), а холодные отдаёт штатному Windows Memory Compression. Горячесть
/// вычисляется по тому, что процесс сам возвращает после вытеснения (стиль DAMON), и затухает со временем.
/// Без драйвера, Hyper-V, активации Windows; для своих приложений без прав администратора. Windows 8+ (prefetch).
/// </summary>
public sealed class RealtimeCompressionTab : TabPage
{
    readonly TextBox _log;
    readonly ComboBox _profile;
    readonly Button _probe, _toggle;
    readonly CheckBox _skipForeground;
    readonly Label _headline;
    readonly Func<bool> _busyElsewhere;
    CancellationTokenSource? _cts;
    bool _running;

    static readonly (string name, string procs)[] Profiles =
    {
        ("Chrome / Edge / Chromium", "chrome,msedge,chromium,brave,opera,yandex"),
        ("Telegram", "Telegram"),
        ("Все приложения", ""),
    };

    public RealtimeCompressionTab(TextBox log, Func<bool> busyElsewhere) : base("Сжатие в реальном времени")
    {
        _log = log; _busyElsewhere = busyElsewhere;
        _headline = new Label { Font = new Font(FontFamily.GenericSansSerif, 12, FontStyle.Bold), AutoSize = false, Height = 50, Text = "Экспериментально. Сначала «Проверить возможности»." };
        _profile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
        foreach (var p in Profiles) _profile.Items.Add(p.name);
        _profile.SelectedIndex = 0;
        _probe = new Button { Text = "🔬 Проверить возможности", AutoSize = true };
        _toggle = new Button { Text = "▶ Включить сжатие в реальном времени", AutoSize = true, Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold) };
        _skipForeground = new CheckBox { Text = "Не трогать активное окно", Checked = true, AutoSize = true };
        _probe.Click += async (_, _) => await Probe();
        _toggle.Click += (_, _) => Toggle();

        var stack = new VStack();
        stack.Add(Ui.L("Экспериментально. Движок учится, к каким областям памяти приложение обращается, держит их горячими через PrefetchVirtualMemory, а остальное отдаёт встроенному сжатию Windows. Цель — чтобы под сжатием была бо́льшая часть памяти работающего приложения, а задержка возврата оставалась незаметной."));
        stack.Add(_headline);
        stack.Add(Ui.Row(Ui.L("Приложение:"), _profile, _probe));
        stack.Add(Ui.Row(_toggle, _skipForeground));
        stack.Add(Ui.Hint("«Проверить возможности» ничего надолго не меняет: один раз вытесняет и возвращает память выбранного приложения и замеряет, поддерживается ли prefetch, как быстро Windows сжимает и сколько памяти остаётся сжатой. Начните с неё."));
        stack.Add(_log, fill: true);
        Controls.Add(stack);
    }

    HashSet<int> Pids(out string[] names)
    {
        string spec = Profiles[_profile.SelectedIndex].procs;
        names = spec.Length == 0 ? Array.Empty<string>() : spec.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var want = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var pids = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            try { if (names.Length == 0 || want.Contains(p.ProcessName)) pids.Add(p.Id); } catch { }
            finally { p.Dispose(); }
        }
        return pids;
    }

    static IntPtr Open(int pid) => Native.OpenProcess(
        Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_SET_QUOTA | Native.PROCESS_VM_OPERATION | Native.PROCESS_SET_INFORMATION, false, pid);

    void Append(string s)
    {
        if (InvokeRequired) { BeginInvoke(() => Append(s)); return; }
        _log.AppendText(s.TrimEnd() + Environment.NewLine);
    }
    static string Mib(long b) => (b >= 0 ? "" : "−") + (Math.Abs(b) / (1024.0 * 1024.0)).ToString("0") + " МиБ";

    async Task Probe()
    {
        if (_running || _busyElsewhere()) { Append("Занято другой операцией."); return; }
        _probe.Enabled = false;
        try
        {
            if (!OperatingSystem.IsWindows()) { Append("Только для Windows."); return; }
            var pidSet = Pids(out _);
            var result = await Task.Run(() => RunProbe(pidSet));
            Append(result);
        }
        catch (Exception ex) { Append("Ошибка: " + ex.Message); }
        finally { _probe.Enabled = true; }
    }

    static string RunProbe(HashSet<int> pids)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"== Проверка возможностей {DateTime.Now:HH:mm:ss} ==");
        int target = pids.FirstOrDefault(p => { var h = Open(p); if (h == IntPtr.Zero) return false; bool ok = ResidentRegions.Capture(h) is { Count: > 64 }; Native.CloseHandle(h); return ok; });
        if (target == 0) { sb.AppendLine("Не найдено подходящего процесса приложения (запустите его и откройте пару окон/вкладок)."); return sb.ToString(); }
        IntPtr handle = Open(target);
        if (handle == IntPtr.Zero) { sb.AppendLine("Нет доступа к процессу (для чужого пользователя нужны права администратора)."); return sb.ToString(); }
        try
        {
            using var p = Process.GetProcessById(target);
            sb.AppendLine($"Процесс: {p.ProcessName} (PID {target})");

            var swS = Stopwatch.StartNew();
            var regions = ResidentRegions.Capture(handle); swS.Stop();
            long wsBefore = Native.TryGetMemInfo(handle, out var m0) ? (long)(ulong)m0.WorkingSetSize : 0;
            sb.AppendLine($"Рабочий набор: {Mib(wsBefore)}, приватных регионов 64 КиБ: {regions?.Count ?? 0}, выборка за {swS.ElapsedMilliseconds} мс");

            // Готовим prefetch-диапазоны из половины регионов (как «горячие»).
            var list = (regions ?? new()).Take(Math.Max(1, (regions?.Count ?? 0) / 2)).ToArray();
            var entries = list.Select(r => new Native.MEMORY_RANGE_ENTRY { VirtualAddress = (IntPtr)((long)(r << 16)), NumberOfBytes = (UIntPtr)RealtimeHotSet.RegionBytes }).ToArray();

            var store0 = IdleTrimAgent.CompressionStoreBytes();
            if (!Native.K32EmptyWorkingSet(handle)) { sb.AppendLine("EmptyWorkingSet не сработал."); return sb.ToString(); }
            // Как быстро страницы попадают в сжатое хранилище.
            long wsAfterTrim = Native.TryGetMemInfo(handle, out var m1) ? (long)(ulong)m1.WorkingSetSize : 0;
            System.Threading.Thread.Sleep(300);
            long store1 = IdleTrimAgent.CompressionStoreBytes();
            sb.AppendLine($"После вытеснения: рабочий набор {Mib(wsAfterTrim)} (−{Mib(wsBefore - wsAfterTrim)}); хранилище сжатия +{Mib(store1 - store0)} за 300 мс");

            bool prefetch = false; int err = 0; var swP = Stopwatch.StartNew();
            if (entries.Length > 0) { prefetch = Native.PrefetchVirtualMemory(handle, (UIntPtr)entries.Length, entries, 0); if (!prefetch) err = System.Runtime.InteropServices.Marshal.GetLastWin32Error(); }
            swP.Stop();
            sb.AppendLine(prefetch
                ? $"PrefetchVirtualMemory на чужом процессе: РАБОТАЕТ ({entries.Length} диапазонов за {swP.ElapsedMilliseconds} мс)"
                : $"PrefetchVirtualMemory: НЕ работает (Win32 {err}). Будет запасной путь без упреждающей подгрузки.");

            System.Threading.Thread.Sleep(200);
            long wsAfterPf = Native.TryGetMemInfo(handle, out var m2) ? (long)(ulong)m2.WorkingSetSize : 0;
            sb.AppendLine($"После prefetch горячих: рабочий набор {Mib(wsAfterPf)} (вернулось {Mib(wsAfterPf - wsAfterTrim)} из {Mib(wsBefore - wsAfterTrim)})");
            long store2 = IdleTrimAgent.CompressionStoreBytes();
            sb.AppendLine($"Осталось в сжатом хранилище: +{Mib(store2 - store0)} относительно старта — это холодная память, которая не мешает приложению.");
            sb.AppendLine("Вывод: " + (prefetch
                ? "можно держать горячее в памяти и сжимать холодное в реальном времени. Включайте режим ниже."
                : "prefetch недоступен; режим будет работать, но первая страница после сжатия распакуется по обращению."));
        }
        finally { Native.CloseHandle(handle); }
        return sb.ToString();
    }

    void Toggle()
    {
        if (_running) { _cts?.Cancel(); return; }
        if (_busyElsewhere()) { Append("Занято другой операцией."); return; }
        _running = true; _toggle.Text = "⏹ Остановить"; _profile.Enabled = _probe.Enabled = false;
        _cts = new CancellationTokenSource();
        var pidSpec = Profiles[_profile.SelectedIndex];
        bool skipFg = _skipForeground.Checked;
        _ = Task.Run(() => Loop(pidSpec.procs, skipFg, _cts.Token))
            .ContinueWith(_ => BeginInvoke(() => { _running = false; _toggle.Text = "▶ Включить сжатие в реальном времени"; _profile.Enabled = _probe.Enabled = true; }));
    }

    // Один цикл: вытеснить рабочий набор -> короткое окно, процесс сам возвращает нужное (сигнал горячести) ->
    // учесть -> вернуть исторически горячее через prefetch -> замерить отклик и возвраты -> подстроить порог.
    void Loop(string spec, bool skipForeground, CancellationToken ct)
    {
        var want = new HashSet<string>(spec.Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        var hot = new Dictionary<int, RealtimeHotSet>();
        var sys0 = SysMem.Take();
        long cycles = 0, totalPrefetched = 0; bool prefetchWorks = true;
        Append($"Старт. Доступно ОЗУ {Mib(sys0.Available)}, хранилище сжатия {Mib(sys0.Store)}.");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int fgPid = skipForeground ? Native.ForegroundPid() : -1;
                var targets = new List<(int pid, IntPtr h)>();
                foreach (var p in Process.GetProcesses())
                {
                    try { if ((spec.Length == 0 || want.Contains(p.ProcessName)) && p.Id != Native.GetCurrentProcessId() && p.Id != fgPid) { var h = Open(p.Id); if (h != IntPtr.Zero) targets.Add((p.Id, h)); } }
                    catch { }
                    finally { p.Dispose(); }
                }
                long prefetchedThisCycle = 0; double refaultSum = 0; int refaultN = 0;
                var winPids = new HashSet<int>(targets.Select(t => t.pid));
                foreach (var (pid, h) in targets)
                {
                    if (!hot.TryGetValue(pid, out var model)) { model = new RealtimeHotSet(); hot[pid] = model; }
                    long wsBefore = Native.TryGetMemInfo(h, out var mb) ? (long)(ulong)mb.WorkingSetSize : 0;
                    Native.K32EmptyWorkingSet(h);
                    // Короткое окно: приложение само возвращает то, что ему срочно нужно = горячее сейчас.
                    System.Threading.Thread.Sleep(250);
                    var accessed = ResidentRegions.Capture(h);
                    if (accessed != null) model.Observe(accessed);
                    long wsAccessed = Native.TryGetMemInfo(h, out var ma) ? (long)(ulong)ma.WorkingSetSize : 0;
                    if (wsBefore > 0) { refaultSum += Math.Min(1.0, (double)Math.Max(0, wsAccessed) / wsBefore); refaultN++; }
                    // Вернуть исторически горячее, чего приложение ещё не подтянуло.
                    var hotRegions = model.HotRegions();
                    if (accessed != null) hotRegions.ExceptWith(accessed);
                    if (hotRegions.Count > 0 && prefetchWorks)
                    {
                        var entries = hotRegions.Select(r => new Native.MEMORY_RANGE_ENTRY { VirtualAddress = (IntPtr)((long)(r << 16)), NumberOfBytes = (UIntPtr)RealtimeHotSet.RegionBytes }).ToArray();
                        if (Native.PrefetchVirtualMemory(h, (UIntPtr)entries.Length, entries, 0)) { prefetchedThisCycle += (long)entries.Length * RealtimeHotSet.RegionBytes; }
                        else prefetchWorks = false;
                    }
                    Native.CloseHandle(h);
                    if (ct.IsCancellationRequested) break;
                }
                double resp = 0; int rn = 0;
                foreach (var w in UiProbe.Windows(winPids).Take(6)) { resp += UiProbe.Ping(w); rn++; }
                resp = rn > 0 ? resp / rn : 0;
                double refault = refaultN > 0 ? refaultSum / refaultN : 0;
                foreach (var model in hot.Values) model.Feedback(resp, refault);
                totalPrefetched += prefetchedThisCycle; cycles++;

                var sys = SysMem.Take();
                Append($"Цикл {cycles}: приложений {targets.Count}, доступно ОЗУ {Mib(sys.Available)} (с начала {(sys.Available - sys0.Available >= 0 ? "+" : "")}{Mib(sys.Available - sys0.Available)}), хранилище сжатия {Mib(sys.Store)} (+{Mib(sys.Store - sys0.Store)}), горячих возвращено {Mib(prefetchedThisCycle)}, отклик окна {resp:F0} мс, возврат рабочего набора {refault * 100:F0}%" + (prefetchWorks ? "" : "  [prefetch недоступен]"));

                for (int i = 0; i < 16 && !ct.IsCancellationRequested; i++) System.Threading.Thread.Sleep(250); // ~4 c между циклами
            }
        }
        catch (Exception ex) { Append("Остановлено: " + ex.Message); }
        Append($"Завершено. Циклов: {cycles}.");
    }
}
