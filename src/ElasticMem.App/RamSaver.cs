using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ElasticMem.App;

/// <summary>
/// «Освободить память» — один клик, без драйвера, WHP, Hyper-V, прав администратора (для своих приложений)
/// и без зависимости от активации Windows. Работает на Windows 10 и новее через штатный механизм ОС:
/// EmptyWorkingSet вытесняет холодные страницы процесса, а Windows Memory Compression сжимает их.
/// Измеряется реальная общесистемная память до/после (GlobalMemoryStatusEx + GetPerformanceInfo).
/// </summary>
public sealed class RamSaverTab : TabPage
{
    [StructLayout(LayoutKind.Sequential)]
    struct PerfInfo { public uint cb; public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize; public uint HandleCount, ProcessCount, ThreadCount; }
    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "K32GetPerformanceInfo")]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetPerformanceInfo(out PerfInfo info, uint size);

    // Ядро системы и то, что нельзя трогать без рывков: эти процессы пропускаем всегда.
    static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "services", "lsass", "winlogon",
        "Memory Compression", "MemCompression", "MsMpEng", "dwm", "audiodg", "fontdrvhost", "LsaIso",
        "Secure System", "vmmem", "vmmemWSL", "csrss.exe", "ElasticMem",
    };
    // Известные игры с античитом: чтение/сжатие их памяти может встревожить защиту — не трогаем.
    static readonly HashSet<string> AntiCheat = new(StringComparer.OrdinalIgnoreCase)
    {
        "dota2", "csgo", "cs2", "valorant", "vgc", "vgtray", "FortniteClient-Win64-Shipping",
        "r5apex", "EasyAntiCheat", "BEService", "BattlEye",
    };

    readonly TextBox _log;
    readonly Button _run;
    readonly CheckBox _skipForeground, _allUsers, _repeat;
    readonly Label _headline;
    bool _busy;

    public RamSaverTab(TextBox log) : base("Освободить память")
    {
        _log = log;
        _headline = new Label { Font = new Font(FontFamily.GenericSansSerif, 13, FontStyle.Bold), AutoSize = false, Height = 54, Text = "Нажмите «Освободить память сейчас»." };
        _run = new Button { Text = "🧹  Освободить память сейчас", AutoSize = true, Font = new Font(FontFamily.GenericSansSerif, 11, FontStyle.Bold), Padding = new Padding(10, 8, 10, 8) };
        _skipForeground = new CheckBox { Text = "Не трогать активное окно", Checked = true, AutoSize = true };
        _allUsers = new CheckBox { Text = "Все пользователи (нужны права администратора)", Checked = false, AutoSize = true };
        _repeat = new CheckBox { Text = "Повторять каждые 60 секунд", Checked = false, AutoSize = true };
        _run.Click += async (_, _) => await RunOnce();
        _repeat.CheckedChanged += (_, _) => { if (_repeat.Checked) _ = Loop(); };

        var stack = new VStack();
        stack.Add(Ui.L("Освобождает оперативную память штатными средствами Windows: холодные страницы приложений вытесняются и сжимаются встроенным Memory Compression. Работает на Windows 10 и новее, без драйвера, Hyper-V и активации Windows. Для своих приложений права администратора не нужны."));
        stack.Add(_headline);
        stack.Add(Ui.Row(_run));
        stack.Add(Ui.Row(_skipForeground, _allUsers, _repeat));
        stack.Add(Ui.Hint("Память уходит в сжатое хранилище Windows и в список ожидания — она сразу доступна другим программам. При обращении к приложению его страницы распаковываются автоматически (обычно незаметно)."));
        stack.Add(_log, fill: true);
        Controls.Add(stack);
    }

    static (ulong total, ulong avail, ulong commit, ulong cache) Snapshot()
    {
        var m = Native.MemoryStatus();
        ulong commit = 0, cache = 0;
        if (GetPerformanceInfo(out var pi, (uint)Marshal.SizeOf<PerfInfo>())) { commit = (ulong)pi.CommitTotal * pi.PageSize; cache = (ulong)pi.SystemCache * pi.PageSize; }
        return (m.ullTotalPhys, m.ullAvailPhys, commit, cache);
    }

    static string Mib(long b) => (b >= 0 ? "" : "−") + (Math.Abs(b) / (1024.0 * 1024.0)).ToString("0") + " МиБ";

    async Task Loop()
    {
        while (_repeat.Checked && !IsDisposed)
        {
            if (!_busy) await RunOnce();
            try { await Task.Delay(60_000); } catch { }
        }
    }

    async Task RunOnce()
    {
        if (_busy) return;
        _busy = true; _run.Enabled = false;
        try
        {
            if (!OperatingSystem.IsWindows()) { Append("Только для Windows."); return; }
            bool skipFg = _skipForeground.Checked, allUsers = _allUsers.Checked;
            var result = await Task.Run(() => TrimAll(skipFg, allUsers));
            _headline.Text = $"Освобождено в доступную память: {Mib(result.freedToAvailable)}   ·   обработано приложений: {result.trimmed}";
            Append(result.text);
        }
        catch (Exception ex) { Append("Ошибка: " + ex.Message); }
        finally { _busy = false; _run.Enabled = true; }
    }

    sealed record Result(long freedToAvailable, long inUseDrop, int trimmed, int skipped, string text);

    static Result TrimAll(bool skipForeground, bool allUsers)
    {
        var sw = Stopwatch.StartNew();
        var before = Snapshot();
        Native.ProcessIdToSessionId(Native.GetCurrentProcessId(), out int mySession);
        int fgPid = Native.ForegroundPid();
        string fgFamily = "";
        if (skipForeground && fgPid > 0) { try { using var p = Process.GetProcessById(fgPid); fgFamily = IdleTrimAgent.FamilyOf(p.ProcessName); } catch { } }

        int trimmed = 0, skipped = 0; long perProcessDrop = 0;
        var top = new List<(string name, long drop)>();
        foreach (var p in Process.GetProcesses())
        {
            string name = p.ProcessName;
            try
            {
                if (p.Id <= 4 || Critical.Contains(name) || AntiCheat.Contains(name)) { skipped++; continue; }
                if (skipForeground && fgFamily.Length > 0 && IdleTrimAgent.FamilyOf(name) == fgFamily) { skipped++; continue; }
                Native.ProcessIdToSessionId(p.Id, out int session);
                if (!allUsers && session != mySession) { skipped++; continue; }

                IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_QUOTA, false, p.Id);
                if (h == IntPtr.Zero) { skipped++; continue; }
                try
                {
                    // Пропускаем GPU- и аудио-процессы Chromium/Electron: их сжатие даёт рывки картинки и заикания звука.
                    var role = ProcRoles.Classify(Native.CommandLine(h));
                    if (ProcRoles.NeverTrim(role)) { skipped++; continue; }
                    long wsBefore = Native.TryGetMemInfo(h, out var a) ? (long)(ulong)a.WorkingSetSize : 0;
                    if (!Native.K32EmptyWorkingSet(h)) { skipped++; continue; }
                    long wsAfter = Native.TryGetMemInfo(h, out var b) ? (long)(ulong)b.WorkingSetSize : wsBefore;
                    long drop = wsBefore - wsAfter;
                    trimmed++; perProcessDrop += Math.Max(0, drop);
                    if (drop > 0) top.Add((name, drop));
                }
                finally { Native.CloseHandle(h); }
            }
            catch { skipped++; }
            finally { p.Dispose(); }
        }

        System.Threading.Thread.Sleep(400); // дать диспетчеру памяти опубликовать изменения
        var after = Snapshot();
        long freed = (long)after.avail - (long)before.avail;
        long inUseDrop = (long)(before.total - before.avail) - (long)(after.total - after.avail);

        var sb = new StringBuilder();
        sb.AppendLine($"== {DateTime.Now:HH:mm:ss}  за {sw.ElapsedMilliseconds} мс ==");
        sb.AppendLine($"Обработано приложений: {trimmed}, пропущено: {skipped}");
        sb.AppendLine($"Сумма рабочего набора приложений: −{Mib(perProcessDrop)}");
        sb.AppendLine($"Доступно ОЗУ: было {Mib((long)before.avail)}, стало {Mib((long)after.avail)}  →  освобождено {Mib(freed)}");
        sb.AppendLine($"Занято ОЗУ: было {Mib((long)(before.total - before.avail))}, стало {Mib((long)(after.total - after.avail))}  →  −{Mib(inUseDrop)}");
        sb.AppendLine($"Выделено (commit) всего: {Mib((long)after.commit)} из физических {Mib((long)after.total)}");
        if (top.Count > 0)
        {
            sb.AppendLine("Больше всего вытеснено:");
            foreach (var t in top.OrderByDescending(t => t.drop).Take(8).GroupBy(t => t.name))
                sb.AppendLine($"  {t.Key}: −{Mib(t.Sum(x => x.drop))}");
        }
        sb.AppendLine("Примечание: освобождённое уходит в сжатое хранилище Windows и список ожидания; это реально переиспользуемая память, а не выгрузка на диск.");
        return new Result(freed, inUseDrop, trimmed, skipped, sb.ToString());
    }

    void Append(string s)
    {
        if (InvokeRequired) { BeginInvoke(() => Append(s)); return; }
        _log.AppendText(s.TrimEnd() + Environment.NewLine + Environment.NewLine);
    }
}
