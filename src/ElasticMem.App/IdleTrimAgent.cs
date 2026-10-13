using System.Diagnostics;

namespace ElasticMem.App;

public sealed class AgentSettings
{
    public bool AllProcesses = true;
    public string TargetProcessName = "";     // без .exe, все экземпляры; можно несколько через запятую
    public AppProfile? Profile;               // профиль приложения (все его процессы)
    public double MinIdleSec = 30;            // минимальное время простоя перед сжатием
    public double MaxIdleSec = 900;           // предел штрафа
    public double CpuThresholdPercent = 0.5;  // ниже — процесс считается простаивающим
    public bool SkipForeground = true;        // активное приложение целиком (все его процессы) не трогаем
    public bool CurrentSessionOnly = true;
    public long MinWorkingSetMb = 30;
    public long RegrowthMb = 15;              // повторно сжимаем, только если рабочий набор вырос
    public double RefaultWindowSec = 20;      // окно, в котором оцениваются «возвраты» страниц
    // v0.5
    public bool GameMode = true;              // полноэкранное приложение/игра впереди → фон сжимается быстрее
    public bool LowerMemoryPriority = true;   // простаивающим процессам — низкий приоритет памяти
    public bool ExcludeGames = true;          // игры с античитом (Dota 2 и т. п.) агент не трогает
    public bool SoftTrim = true;              // сжимать ступенями, начиная с самых старых страниц
    // v0.6: постоянное сжатие (и активных приложений)
    public bool AlwaysOn = true;              // держать рабочий набор у «горячего» размера всё время, а не только в простое
    public double BgBudgetMbps = 2;           // допустимый возврат сжатых страниц, МБ/с, для фоновых (≈0,3% ядра на распаковку)
    public double FgBudgetMbps = 4;           // для активного приложения
    public bool AlwaysIncludeActive = false;  // v0.8: активное приложение — только экспериментально (по умолчанию выкл.)
}

public sealed class ProcRow
{
    public int Pid; public string Name = ""; public string Role = ""; public string Level = "";
    public long WorkingSet; public string State = ""; public double IdleSec; public double DwellSec;
    public long TrimmedTotal; public int Trims; public double LastRefaultPct = -1; public bool Access;
    public string Family = ""; public long Target, Peak; public double RateMbps; public bool CapUseless; public bool Protected;
}

/// <summary>
/// Агент «сжатия между периодами активности», v0.5.
/// • Приложение — это семейство процессов (Chrome, Steam, Яндекс Музыка): если активно окно семейства, не трогаем ни один его процесс.
/// • GPU- и аудио-процессы Chromium/Electron не трогаем никогда (рывки картинки, заикание звука).
/// • Мягкое сжатие: временный жёсткий предел рабочего набора — Windows сама выбирает самые старые страницы;
///   доля растёт 50% → 75% → 100%, пока возврат мал, и уменьшается, если процесс страницы «втягивает».
/// • Возврат меряется точно: по выборке собственных страниц процесса (QueryWorkingSetEx), а не по росту рабочего набора.
/// • Игровой режим: когда впереди полноэкранная игра, фоновые приложения сжимаются втрое быстрее и получают низкий приоритет памяти.
/// </summary>
public sealed class IdleTrimAgent : IDisposable
{
    private sealed class St
    {
        public int Pid; public string Name = ""; public string Family = ""; public ProcRole Role; public IntPtr H;
        public bool CanTrim, CanProbe, CanPrio;
        public long LastCpu; public DateTime LastSample; public DateTime IdleSince;
        public double Dwell; public double Level = 0.5; public DateTime LastTrim = DateTime.MinValue;
        public long WsAfterTrim, TrimmedBytes, TrimmedTotal; public uint FaultsAtTrim; public bool Evaluating;
        public int Trims; public double LastRefaultPct = -1; public long Ws; public string State = "";
        public WsProbe? Probe; public double ProbeAfter;
        public bool CapOn, CapUseless; public DateTime CapAt; public UIntPtr OrigMin, OrigMax; public uint OrigFlags; public long CapBefore, CapTarget;
        public bool PrioLow;
        // постоянный режим
        public long CTarget, CPeak, CTrimmed, CBefore, CAfter; public double CRate; public DateTime CNext, CLast;
        public WsProbe? CProbe; public double CProbeAfter = -1; public bool CPulse, CFg;
    }

    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "services", "lsass", "winlogon", "Memory Compression",
        "MsMpEng", "dwm", "audiodg", "fontdrvhost", "LsaIso", "Secure System", "vmmem", "MemCompression", "svchost",
        "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "TextInputHost", "ctfmon", "sihost",
    };

    private readonly Dictionary<int, St> _st = new();
    private readonly object _lock = new();
    private System.Threading.Timer? _timer;
    private DateTime _lastScan = DateTime.MinValue;
    private readonly int _self = Native.GetCurrentProcessId();
    private int _mySession;
    private bool _gameModeLogged;
    public AgentSettings Settings { get; set; } = new();
    public event Action<string>? Log;
    public bool Running => _timer != null;
    public long TotalTrimmed { get; private set; }
    public bool GameModeActive { get; private set; }

    public void Start()
    {
        if (_timer != null) return;
        Native.ProcessIdToSessionId(_self, out _mySession);
        _timer = new System.Threading.Timer(_ => SafeTick(), null, 0, 1000);
        Log?.Invoke(Settings.AllProcesses ? "Агент запущен: все приложения" : $"Агент запущен: только «{Settings.Profile?.Title ?? Settings.TargetProcessName}»");
    }

    public void Stop()
    {
        _timer?.Dispose(); _timer = null;
        lock (_lock)
        {
            foreach (var s in _st.Values) { Restore(s); Native.CloseHandle(s.H); }
            _st.Clear();
        }
        Log?.Invoke("Агент остановлен (пределы и приоритеты памяти восстановлены)");
    }

    public async Task StopAndWaitAsync()
    {
        var timer=Interlocked.Exchange(ref _timer,null);if(timer!=null)await timer.DisposeAsync();
        lock(_lock){foreach(var s in _st.Values){Restore(s);Native.CloseHandle(s.H);}_st.Clear();}
        Log?.Invoke("Агент остановлен; callback таймера завершён, пределы и приоритеты восстановлены");
    }

    public void Dispose() => Stop();

    public List<ProcRow> Snapshot()
    {
        lock (_lock)
            return _st.Values.Select(s => new ProcRow
            {
                Pid = s.Pid, Name = s.Name, Role = ProcRoles.Ru(s.Role), Level = $"{s.Level * 100:F0}%", WorkingSet = s.Ws, State = s.State, Access = s.CanTrim,
                IdleSec = (DateTime.UtcNow - s.IdleSince).TotalSeconds, DwellSec = s.Dwell,
                TrimmedTotal = s.TrimmedTotal, Trims = s.Trims, LastRefaultPct = s.LastRefaultPct,
                Family = s.Family, Target = s.CTarget, Peak = s.CPeak, RateMbps = s.CRate / (1 << 20), CapUseless = s.CapUseless,
                Protected = ProcRoles.NeverTrim(s.Role),
            }).OrderByDescending(r => r.WorkingSet).ToList();
    }

    /// <summary>Сводка для журнала: сколько держим, сколько отжато, работает ли мягкий предел.</summary>
    public string Summary()
    {
        var rows = Snapshot();
        if (rows.Count == 0) return "Сводка агента: процессов нет";
        var sb = new System.Text.StringBuilder();
        sb.Append($"Сводка агента{(Settings.AlwaysOn ? " (всегда)" : "")}: процессов {rows.Count}, рабочий набор {rows.Sum(r => r.WorkingSet) >> 20} МБ, " +
                  $"пик {rows.Sum(r => Math.Max(r.Peak, r.WorkingSet)) >> 20} МБ, отжато всего {TotalTrimmed >> 20} МБ, без мягкого предела: {rows.Count(r => r.CapUseless)}");
        foreach (var g in rows.GroupBy(r => r.Family).OrderByDescending(g => g.Sum(r => r.WorkingSet)).Take(8))
            sb.Append($"; {g.Key}: {g.Sum(r => r.WorkingSet) >> 20}/{g.Sum(r => Math.Max(r.Peak, r.WorkingSet)) >> 20} МБ, возврат {g.Sum(r => r.RateMbps):F1} МБ/с");
        return sb.ToString();
    }

    public static string FamilyOf(string processName)
    {
        foreach (var p in AppProfile.Builtin) if (p.Matches(processName)) return p.Title;
        return processName;
    }

    private static bool IsGame(string processName) =>
        AppProfile.Builtin.Any(p => p.AntiCheatRisk && p.Matches(processName)) || AppProfile.KnownGames.Contains(processName);

    private bool IsTarget(Process p)
    {
        if (p.Id == _self || p.Id <= 4 || Protected.Contains(p.ProcessName)) return false;
        if (Settings.CurrentSessionOnly && p.SessionId != _mySession) return false;
        if (Settings.ExcludeGames && IsGame(p.ProcessName)) return false;
        if (Settings.AllProcesses) return true;
        if (Settings.Profile != null) return Settings.Profile.Matches(p.ProcessName);
        foreach (var raw in Settings.TargetProcessName.Split(',', ';'))
        {
            var t = raw.Trim();
            if (t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) t = t[..^4];
            if (t.Length > 0 && string.Equals(p.ProcessName, t, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private void Rescan()
    {
        var now = DateTime.UtcNow;
        var alive = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (!IsTarget(p)) continue;
                alive.Add(p.Id);
                if (_st.ContainsKey(p.Id)) continue;
                uint full = Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_VM_READ |
                            Native.PROCESS_SET_QUOTA | Native.PROCESS_SET_INFORMATION;
                bool probe = true, prio = true, trim = true;
                var h = Native.OpenProcess(full, false, p.Id);
                if (h == IntPtr.Zero) { probe = false; h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_QUOTA | Native.PROCESS_SET_INFORMATION, false, p.Id); }
                if (h == IntPtr.Zero) { prio = false; h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_QUOTA, false, p.Id); }
                if (h == IntPtr.Zero) { trim = false; h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id); }
                if (h == IntPtr.Zero) continue;
                Native.GetProcessTimes(h, out _, out _, out long k, out long u);
                var role = ProcRoles.Classify(Native.CommandLine(h));
                // защита от «застрявшего» жёсткого предела (если прошлый запуск агента был убит во время импульса)
                if (trim && Native.GetProcessWorkingSetSizeEx(h, out var mn0, out var mx0, out uint fl0) && (fl0 & Native.QUOTA_LIMITS_HARDWS_MAX_ENABLE) != 0)
                {
                    Native.SetProcessWorkingSetSizeEx(h, mn0, mx0, Native.QUOTA_LIMITS_HARDWS_MAX_DISABLE | Native.QUOTA_LIMITS_HARDWS_MIN_DISABLE);
                    Log?.Invoke($"{p.ProcessName} ({p.Id}): найден жёсткий предел рабочего набора — снят");
                }
                _st[p.Id] = new St
                {
                    Pid = p.Id, Name = p.ProcessName, Family = FamilyOf(p.ProcessName), Role = role, H = h,
                    CanTrim = trim, CanProbe = probe, CanPrio = prio, LastCpu = k + u, LastSample = now,
                    IdleSince = now, Dwell = Settings.MinIdleSec, State = trim ? "наблюдение" : "нет прав (нужен админ)",
                };
            }
        }
        foreach (var pid in _st.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            Native.CloseHandle(_st[pid].H);
            _st.Remove(pid);
        }
    }

    private void SafeTick()
    {
        try { Tick(); } catch (Exception ex) { Log?.Invoke("Ошибка агента: " + ex.Message); }
    }

    private void Restore(St s)
    {
        if (s.CapOn) ReleaseCap(s);
        if (s.PrioLow) { Native.SetMemoryPriority(s.H, Native.MEMORY_PRIORITY_NORMAL); s.PrioLow = false; }
    }

    private void ReleaseCap(St s)
    {
        s.CapOn = false;
        Native.SetProcessWorkingSetSizeEx(s.H, s.OrigMin, s.OrigMax,
            Native.QUOTA_LIMITS_HARDWS_MIN_DISABLE | Native.QUOTA_LIMITS_HARDWS_MAX_DISABLE);
    }

    private void Tick()
    {
        var cfg = Settings;
        var now = DateTime.UtcNow;
        int fg = Native.ForegroundPid();
        string fgName = "";
        if (fg > 0) { try { using var fp = Process.GetProcessById(fg); fgName = fp.ProcessName; } catch { } }
        string fgFamily = fgName.Length > 0 ? FamilyOf(fgName) : "";
        bool game = cfg.GameMode && fgName.Length > 0 && !Protected.Contains(fgName) && (IsGame(fgName) || Native.ForegroundIsFullscreen());
        if (game != GameModeActive)
        {
            GameModeActive = game;
            if (game) Log?.Invoke($"Игровой режим: впереди «{fgName}» — фоновые приложения сжимаются быстрее");
            else if (_gameModeLogged) Log?.Invoke("Игровой режим выключен");
            _gameModeLogged = true;
        }

        lock (_lock)
        {
            if(_timer==null)return;
            if ((now - _lastScan).TotalSeconds >= 5) { Rescan(); _lastScan = now; }
            int cores = Environment.ProcessorCount;
            var pulses = new List<St>();
            foreach (var s in _st.Values)
            {
                if (!Native.GetProcessTimes(s.H, out _, out long exit, out long k, out long u)) continue;
                if (!Native.TryGetMemInfo(s.H, out var mi)) continue;
                s.Ws = (long)mi.WorkingSetSize;
                double dt = (now - s.LastSample).TotalSeconds;
                double cpuPct = dt > 0 ? (k + u - s.LastCpu) / 1e7 / dt / cores * 100 : 0;
                s.LastCpu = k + u; s.LastSample = now;

                // 1) снять временный предел мягкого сжатия
                if (s.CapOn && (now - s.CapAt).TotalSeconds >= 1)
                {
                    ReleaseCap(s);
                    long dropped = Math.Max(0, s.CapBefore - s.Ws);
                    long wanted = Math.Max(1, s.CapBefore - s.CapTarget);
                    if (dropped < wanted / 10)
                    {
                        s.CapUseless = true; // система не стала урезать по пределу — дальше полное сжатие
                        Log?.Invoke($"{s.Name} ({s.Pid}): мягкое сжатие не сработало, переход на полное");
                        FullTrim(s, s.CapBefore, now, "полное");
                    }
                    else FinishTrim(s, s.CapBefore, s.Ws, now);
                }

                // 2) оценка последствий: какая доля сжатых страниц вернулась
                if (s.Evaluating && (now - s.LastTrim).TotalSeconds >= cfg.RefaultWindowSec)
                {
                    s.Evaluating = false;
                    double pct = -1;
                    if (s.Probe != null && s.ProbeAfter >= 0)
                    {
                        double r1 = s.Probe.ResidentFraction(s.H);
                        if (r1 >= 0 && s.ProbeAfter < 0.98) pct = 100 * Math.Clamp((r1 - s.ProbeAfter) / (1 - s.ProbeAfter), 0, 1);
                    }
                    if (pct < 0)
                    {
                        long refault = (long)(mi.PageFaultCount - s.FaultsAtTrim) * 4096;
                        pct = s.TrimmedBytes > 0 ? 100.0 * Math.Min(refault, s.TrimmedBytes) / s.TrimmedBytes : 0;
                    }
                    s.LastRefaultPct = pct;
                    s.Probe = null;
                    if (pct > 35)
                    {
                        s.Dwell = Math.Min(cfg.MaxIdleSec, s.Dwell * 2);
                        s.Level = Math.Max(0.25, s.Level - 0.25);
                        Log?.Invoke($"{s.Name} ({s.Pid}): вернул {pct:F0}% сжатых страниц → задержка {s.Dwell:F0} с, доля {s.Level * 100:F0}%");
                    }
                    else if (pct < 10)
                    {
                        s.Dwell = Math.Max(cfg.MinIdleSec, s.Dwell * 0.75);
                        s.Level = Math.Min(1.0, s.Level + 0.25);
                    }
                }

                bool fgFam = cfg.SkipForeground && fgFamily.Length > 0 && s.Family == fgFamily;
                if (cfg.AlwaysOn) { Continuous(s, cfg, now, s.Pid == fg || (fgFamily.Length > 0 && s.Family == fgFamily), game, pulses); continue; }

                // 3) активен ли процесс (или его приложение)
                bool active = cpuPct > cfg.CpuThresholdPercent || s.Pid == fg || fgFam;
                if (active)
                {
                    s.IdleSince = now;
                    if (s.PrioLow) { Native.SetMemoryPriority(s.H, Native.MEMORY_PRIORITY_NORMAL); s.PrioLow = false; }
                    s.State = s.Pid == fg || fgFam ? "активное приложение" : $"активен ({cpuPct:F1}% CPU)";
                    continue;
                }
                if (s.CapOn) { s.State = "сжимается…"; continue; }
                double idle = (now - s.IdleSince).TotalSeconds;
                double dwell = game ? Math.Max(10, s.Dwell / 3) : s.Dwell;
                if (!s.CanTrim) { s.State = "нет прав (нужен админ)"; continue; }
                if (ProcRoles.NeverTrim(s.Role)) { s.State = $"защищён ({ProcRoles.Ru(s.Role)})"; continue; }
                if (s.Ws < cfg.MinWorkingSetMb << 20) { s.State = "мал для сжатия"; continue; }
                if (idle < dwell) { s.State = $"простой {idle:F0}/{dwell:F0} с{(game ? " (игра)" : "")}"; continue; }
                if (s.Trims > 0 && s.Ws - s.WsAfterTrim < cfg.RegrowthMb << 20) { s.State = "сжат, ждёт роста"; continue; }

                if (cfg.LowerMemoryPriority && s.CanPrio && !s.PrioLow && Native.SetMemoryPriority(s.H, Native.MEMORY_PRIORITY_LOW)) s.PrioLow = true;
                s.Probe = s.CanProbe && s.Ws < (3L << 30) ? WsProbe.Capture(s.H, 8192, s.Pid) : null;
                s.ProbeAfter = -1;
                if (!cfg.SoftTrim || s.CapUseless || s.Level >= 0.999) FullTrim(s, s.Ws, now, $"простой {idle:F0} с");
                else
                {
                    if (!Native.GetProcessWorkingSetSizeEx(s.H, out s.OrigMin, out s.OrigMax, out s.OrigFlags)) { FullTrim(s, s.Ws, now, "полное"); continue; }
                    long target = Math.Max(8L << 20, (long)(s.Ws * (1 - s.Level)));
                    long min = Math.Min((long)(ulong)s.OrigMin, target / 2);
                    if (Native.SetProcessWorkingSetSizeEx(s.H, (UIntPtr)(ulong)min, (UIntPtr)(ulong)target,
                            Native.QUOTA_LIMITS_HARDWS_MAX_ENABLE | Native.QUOTA_LIMITS_HARDWS_MIN_DISABLE))
                    {
                        s.CapOn = true; s.CapAt = now; s.CapBefore = s.Ws; s.CapTarget = target;
                        s.State = $"сжимается {s.Level * 100:F0}%…";
                    }
                    else { s.CapUseless = true; FullTrim(s, s.Ws, now, "полное"); }
                }
            }
            if (pulses.Count > 0) ReleasePulses(pulses, now);
        }
    }

    /// <summary>
    /// Постоянное сжатие: рабочий набор держится около оценки «горячего» объёма. Раз в несколько секунд —
    /// короткий импульс жёсткого предела (200 мс): Windows выталкивает самые старые страницы в Memory Compression,
    /// затем предел снимается и процесс снова растёт свободно. Цель подстраивается по точному возврату:
    /// возврат выше бюджета (МБ/с) → цель ×1,25 + возвращённое; ниже → цель ×0,92 (в игровом режиме для фона ×0,8).
    /// </summary>
    private void Continuous(St s, AgentSettings cfg, DateTime now, bool isFg, bool game, List<St> pulses)
    {
        if (!s.CanTrim) { s.State = "нет прав (нужен админ)"; return; }
        if (ProcRoles.NeverTrim(s.Role)) { s.State = $"защищён ({ProcRoles.Ru(s.Role)})"; return; }
        s.CPeak = Math.Max(s.CPeak, s.Ws);
        if (s.Ws < cfg.MinWorkingSetMb << 20 && s.CTarget == 0) { s.State = "мал для сжатия"; return; }
        if (isFg && !cfg.AlwaysIncludeActive) { s.State = "активное (не трогаем)"; s.CTarget = 0; return; }
        if (cfg.LowerMemoryPriority && s.CanPrio)
        {
            if (!isFg && !s.PrioLow && Native.SetMemoryPriority(s.H, Native.MEMORY_PRIORITY_LOW)) s.PrioLow = true;
            else if (isFg && s.PrioLow && Native.SetMemoryPriority(s.H, Native.MEMORY_PRIORITY_NORMAL)) s.PrioLow = false;
        }
        string st = $"всегда{(isFg ? " (активно)" : "")}: цель {s.CTarget >> 20} МБ, возврат {s.CRate / (1 << 20):F1} МБ/с";
        if (now < s.CNext) { s.State = st; return; }

        double period = Math.Max(0.5, (now - s.CLast).TotalSeconds);
        if (s.CProbe != null && s.CProbeAfter >= 0 && s.CProbeAfter < 0.98)
        {
            double r1 = s.CProbe.ResidentFraction(s.H);
            if (r1 >= 0) s.CRate = Math.Clamp((r1 - s.CProbeAfter) / (1 - s.CProbeAfter), 0, 1) * s.CTrimmed / period;
        }
        else if (s.CLast != default) s.CRate *= 0.5;
        // Отрастание после импульса: возврат ИЛИ новые выделения. В обоих случаях повторное вытеснение — пустая «карусель»,
        // поэтому половина отрастания тоже идёт в счёт бюджета (в v0.7 за 10 мин было вытеснено 36 ГБ при рабочем наборе ~2 ГБ).
        if (s.CLast != default && s.CAfter > 0) s.CRate = Math.Max(s.CRate, 0.5 * Math.Max(0, s.Ws - s.CAfter) / period);
        s.CProbe = null; s.CTrimmed = 0;

        double budget = (isFg ? cfg.FgBudgetMbps : cfg.BgBudgetMbps) * (1 << 20);
        long floor = isFg ? Math.Max(16L << 20, s.CPeak / 10) : 8L << 20;
        if (s.CTarget == 0 || isFg != s.CFg && isFg) s.CTarget = (long)(s.Ws * (isFg ? 0.8 : 0.7));
        else if (s.CRate > budget) s.CTarget = (long)(s.CTarget * 1.25 + s.CRate * period);
        else s.CTarget = (long)(s.CTarget * (game && !isFg ? 0.8 : 0.92));
        s.CTarget = Math.Clamp(s.CTarget, floor, Math.Max(floor, s.CPeak));
        s.CFg = isFg;
        s.CLast = now;
        s.CNext = now.AddSeconds(isFg ? 10 : game ? 3 : 5);

        if (s.Ws > s.CTarget + (s.CTarget >> 4) + (2L << 20) && !(isFg && s.CapUseless))
        {
            if (s.CapUseless) { s.CProbe = s.CanProbe ? WsProbe.Capture(s.H, 4096, s.Pid) : null; FullTrim(s, s.Ws, now, "постоянный режим, полное"); s.CTrimmed = s.TrimmedBytes; s.CProbeAfter = s.CProbe?.ResidentFraction(s.H) ?? -1; s.Evaluating = false; return; }
            if (!Native.GetProcessWorkingSetSizeEx(s.H, out s.OrigMin, out s.OrigMax, out s.OrigFlags)) { s.State = st; return; }
            s.CProbe = s.CanProbe && s.Ws < (3L << 30) ? WsProbe.Capture(s.H, 4096, s.Pid) : null;
            long min = Math.Min((long)(ulong)s.OrigMin, s.CTarget / 2);
            if (Native.SetProcessWorkingSetSizeEx(s.H, (UIntPtr)(ulong)min, (UIntPtr)(ulong)s.CTarget,
                    Native.QUOTA_LIMITS_HARDWS_MAX_ENABLE | Native.QUOTA_LIMITS_HARDWS_MIN_DISABLE))
            {
                s.CapOn = true; s.CPulse = true; s.CBefore = s.Ws; s.CapTarget = s.CTarget; pulses.Add(s);
            }
            else s.CapUseless = true;
        }
        s.State = st;
    }

    private void ReleasePulses(List<St> pulses, DateTime now)
    {
        Thread.Sleep(200);
        foreach (var s in pulses)
        {
            ReleaseCap(s); s.CPulse = false;
            if (!Native.TryGetMemInfo(s.H, out var mi)) continue;
            long after = (long)mi.WorkingSetSize;
            long dropped = Math.Max(0, s.CBefore - after), wanted = Math.Max(1, s.CBefore - s.CapTarget);
            if (dropped < wanted / 10 && wanted > (8L << 20))
            {
                s.CapUseless = true;
                Log?.Invoke($"{s.Name} ({s.Pid}): Windows не урезает по пределу — для фона дальше полное сжатие, активное не трогаем");
            }
            s.CTrimmed = dropped; s.Ws = after; s.CAfter = after; TotalTrimmed += dropped; s.TrimmedTotal += dropped;
            if (dropped > 0) s.Trims++;
            s.CProbeAfter = s.CProbe?.ResidentFraction(s.H) ?? -1;
        }
    }

    private void FullTrim(St s, long before, DateTime now, string why)
    {
        if (Native.K32EmptyWorkingSet(s.H) && Native.TryGetMemInfo(s.H, out var mi2))
        {
            s.Level = 1.0;
            FinishTrim(s, before, (long)mi2.WorkingSetSize, now, why);
        }
        else s.State = "отказ системы";
    }

    private void FinishTrim(St s, long before, long after, DateTime now, string why = "")
    {
        s.TrimmedBytes = Math.Max(0, before - after);
        s.TrimmedTotal += s.TrimmedBytes; TotalTrimmed += s.TrimmedBytes;
        s.WsAfterTrim = after; s.Ws = after;
        if (Native.TryGetMemInfo(s.H, out var mi)) s.FaultsAtTrim = mi.PageFaultCount;
        s.ProbeAfter = s.Probe?.ResidentFraction(s.H) ?? -1;
        s.LastTrim = now; s.Evaluating = true; s.Trims++;
        s.State = $"передан на сжатие ({s.Level * 100:F0}%)";
        Log?.Invoke($"{s.Name} ({s.Pid}, {ProcRoles.Ru(s.Role)}): {(why.Length > 0 ? why + ", " : "")}доля {s.Level * 100:F0}% → рабочий набор {before >> 20} → {after >> 20} МБ");
    }

    /// <summary>Объём хранилища сжатой памяти Windows (рабочий набор процесса «Memory Compression»).</summary>
    public static long CompressionStoreBytes()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("Memory Compression")) using (p) return p.WorkingSet64;
        }
        catch { }
        return -1;
    }
}
