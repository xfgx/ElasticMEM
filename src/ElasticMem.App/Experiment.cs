using System.Diagnostics;
using System.Text;

namespace ElasticMem.App;

/// <summary>
/// Сравнительный эксперимент на одном и том же сценарии пользователя:
///   A — Windows без агента (штатное сжатие);  B — однократное вытеснение в начале фазы;
///   C — «Сжимать всегда» включая активное;    D — «Сжимать всегда» только фон.
/// Меряется вся система (а не сумма рабочих наборов): занятая RAM, списки страниц, хранилище сжатия, коммит,
/// страничные ошибки по типам, чтение/запись страниц с диска, отзывчивость окон, CPU приложений и агента.
/// </summary>
public static class Experiment
{
    public enum Phase { A, B, C, D }

    public static string Name(Phase p) => p switch
    {
        Phase.A => "A: без агента", Phase.B => "B: разовое вытеснение", Phase.C => "C: всегда, вкл. активное", _ => "D: всегда, только фон",
    };

    public const string Scenario =
        "Сценарий (повторяйте одинаково в каждой фазе):\r\n" +
        "  1) 1 мин — обычная работа в Chrome (листайте 2–3 открытые вкладки);\r\n" +
        "  2) переключитесь на давно не открывавшуюся вкладку Chrome и на окно Steam;\r\n" +
        "  3) включите/переключите трек в Яндекс Музыке, откройте чат с медиа в Telegram;\r\n" +
        "  4) остаток фазы — то, что обычно (игра, браузер), без новых тяжёлых программ.";

    public sealed class Stats
    {
        public Phase Phase; public int Round; public double Sec; public int N;
        public double InUse, Avail, Modified, Standby, StandbyLow, Commit, StoreAvg, AppsWs, AppsCpu, AgentCpu;
        public long Store0, Store1, InUse0, InUse1;
        public double FaultsPs, TransitionPs, DemandZeroPs, DiskReadMBps, DiskReadIops, DiskWriteMBps;
        public double UiP50, UiP95, UiMax; public int UiSlow100, UiSlow500, UiSamples;
        public long AgentTrimmed; public bool HasLists, HasPerf;
    }

    private static long AppsWs(List<AppProfile> profs, out double cpuSec, out HashSet<int> pids)
    {
        long ws = 0; cpuSec = 0; pids = new HashSet<int>();
        foreach (var p in profs)
            foreach (var x in p.Find())
            {
                try { ws += x.WorkingSet64; cpuSec += x.TotalProcessorTime.TotalSeconds; pids.Add(x.Id); } catch { }
                x.Dispose();
            }
        return ws;
    }

    /// <summary>Однократное вытеснение всех процессов приложений (кроме GPU/звука), как в фазе B.</summary>
    private static void OneShot(List<AppProfile> profs)
    {
        foreach (var p in profs)
            foreach (var x in p.Find())
            {
                var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_QUOTA, false, x.Id);
                if (h != IntPtr.Zero)
                {
                    if (!ProcRoles.NeverTrim(ProcRoles.Classify(Native.CommandLine(h)))) Native.K32EmptyWorkingSet(h);
                    Native.CloseHandle(h);
                }
                x.Dispose();
            }
    }

    public static Stats RunPhase(Phase ph, int round, List<AppProfile> profs, double minutes, IProgress<string> progress, CancellationToken ct, List<string> log)
    {
        IdleTrimAgent? agent = null;
        if (ph == Phase.B) OneShot(profs);
        if (ph is Phase.C or Phase.D)
        {
            agent = new IdleTrimAgent
            {
                Settings = new AgentSettings
                {
                    AllProcesses = false, TargetProcessName = string.Join(",", profs.SelectMany(p => p.ProcessNames)),
                    AlwaysOn = true, AlwaysIncludeActive = ph == Phase.C, MinWorkingSetMb = 16,
                },
            };
            agent.Log += m => { lock (log) log.Add($"{DateTime.Now:HH:mm:ss} [{ph}{round}] {m}"); };
            agent.Start();
        }
        var self = Process.GetCurrentProcess();
        double selfCpu0 = self.TotalProcessorTime.TotalSeconds;
        var st = new Stats { Phase = ph, Round = round };
        var s0 = SysMem.Take();
        st.Store0 = s0.Store; st.InUse0 = s0.Total - s0.Available; st.HasLists = s0.HasLists; st.HasPerf = s0.HasPerf;
        AppsWs(profs, out double appCpu0, out var pids);
        var wins = UiProbe.Windows(pids);
        var lat = new List<double>();
        var sw = Stopwatch.StartNew();
        double total = minutes * 60;
        var last = s0;
        try
        {
            while (sw.Elapsed.TotalSeconds < total)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(1000);
                var s = SysMem.Take(); last = s;
                long ws = AppsWs(profs, out _, out var p2);
                if (st.N % 10 == 0) { pids = p2; wins = UiProbe.Windows(pids); }
                foreach (var w in wins.Take(6)) lat.Add(UiProbe.Ping(w));
                st.N++;
                st.InUse += s.Total - s.Available; st.Avail += s.Available; st.Modified += s.Modified; st.Standby += s.Standby;
                st.StandbyLow += s.StandbyLow; st.Commit += s.Commit; st.StoreAvg += Math.Max(0, s.Store); st.AppsWs += ws;
                progress.Report($"{Name(ph)} (круг {round}): {sw.Elapsed.TotalSeconds:F0}/{total:F0} с — занято {(s.Total - s.Available) >> 20} МБ, приложения {ws >> 20} МБ, хранилище {s.Store >> 20} МБ");
            }
        }
        finally
        {
            if (agent != null) { st.AgentTrimmed = agent.TotalTrimmed; lock (log) log.Add($"{DateTime.Now:HH:mm:ss} [{ph}{round}] {agent.Summary()}"); agent.Stop(); }
        }
        st.Sec = sw.Elapsed.TotalSeconds;
        int n = Math.Max(1, st.N);
        st.InUse /= n; st.Avail /= n; st.Modified /= n; st.Standby /= n; st.StandbyLow /= n; st.Commit /= n; st.StoreAvg /= n; st.AppsWs /= n;
        st.Store1 = last.Store; st.InUse1 = last.Total - last.Available;
        AppsWs(profs, out double appCpu1, out _);
        st.AppsCpu = Math.Max(0, appCpu1 - appCpu0) / st.Sec / Environment.ProcessorCount * 100;
        self.Refresh();
        st.AgentCpu = (self.TotalProcessorTime.TotalSeconds - selfCpu0) / st.Sec / Environment.ProcessorCount * 100;
        if (s0.HasPerf && last.HasPerf)
        {
            st.FaultsPs = unchecked(last.PageFaults - s0.PageFaults) / st.Sec;
            st.TransitionPs = unchecked(last.Transition - s0.Transition) / st.Sec;
            st.DemandZeroPs = unchecked(last.DemandZero - s0.DemandZero) / st.Sec;
            st.DiskReadMBps = unchecked(last.PageReads - s0.PageReads) * 4096.0 / (1 << 20) / st.Sec;
            st.DiskReadIops = unchecked(last.PageReadIos - s0.PageReadIos) / st.Sec;
            st.DiskWriteMBps = unchecked(last.DirtyWrites - s0.DirtyWrites) * 4096.0 / (1 << 20) / st.Sec;
        }
        if (lat.Count > 0)
        {
            lat.Sort();
            st.UiSamples = lat.Count; st.UiP50 = lat[lat.Count / 2]; st.UiP95 = lat[(int)(lat.Count * 0.95)]; st.UiMax = lat[^1];
            st.UiSlow100 = lat.Count(x => x > 100); st.UiSlow500 = lat.Count(x => x > 500);
        }
        return st;
    }

    public static string Format(List<Stats> all, List<AppProfile> profs, List<string> log)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"══ Сравнительный эксперимент — {DateTime.Now:yyyy-MM-dd HH:mm} ══");
        sb.AppendLine("Приложения: " + string.Join(", ", profs.Select(p => p.Title)));
        if (!all.Any(s => s.HasLists)) sb.AppendLine("ВНИМАНИЕ: списки страниц недоступны — запустите от администратора.");
        var phases = all.Select(s => s.Phase).Distinct().OrderBy(p => p).ToList();
        string Col(Func<Stats, double> f, string fmt) => string.Join("", phases.Select(p => all.Where(s => s.Phase == p).Average(f).ToString(fmt).PadLeft(14)));
        static double M(double b) => b / (1 << 20);
        sb.AppendLine();
        sb.AppendLine("Метрика (среднее по фазе)".PadRight(44) + string.Join("", phases.Select(p => p.ToString().PadLeft(14))));
        void Row(string name, Func<Stats, double> f, string fmt = "F0") => sb.AppendLine(name.PadRight(44) + Col(f, fmt));
        Row("Занятая RAM системы, МБ", s => M(s.InUse));
        Row("  изменение за фазу, МБ", s => M(s.InUse1 - s.InUse0));
        Row("Доступно (свободно + резерв), МБ", s => M(s.Avail));
        Row("Список изменённых страниц, МБ", s => M(s.Modified));
        Row("Резервный список (кеш), МБ", s => M(s.Standby));
        Row("  из него низкий приоритет 0–2, МБ", s => M(s.StandbyLow));
        Row("Хранилище сжатия (физически), МБ", s => M(s.StoreAvg));
        Row("  изменение за фазу, МБ", s => M(s.Store1 - s.Store0));
        Row("Коммит системы, МБ", s => M(s.Commit));
        Row("Рабочие наборы приложений, МБ", s => M(s.AppsWs));
        Row("Страничных ошибок/с (все типы)", s => s.FaultsPs);
        Row("  из резерва/изменённых (transition)/с", s => s.TransitionPs);
        Row("  обнуляемые (новая память)/с", s => s.DemandZeroPs);
        Row("Чтение страниц с диска, МБ/с", s => s.DiskReadMBps, "F2");
        Row("  операций чтения/с", s => s.DiskReadIops, "F1");
        Row("Запись изменённых страниц на диск, МБ/с", s => s.DiskWriteMBps, "F2");
        Row("Отклик окон p50, мс", s => s.UiP50, "F1");
        Row("Отклик окон p95, мс", s => s.UiP95, "F1");
        Row("Отклик окон макс, мс", s => s.UiMax, "F0");
        Row("  ответов > 100 мс", s => s.UiSlow100);
        Row("  ответов > 500 мс", s => s.UiSlow500);
        Row("CPU приложений, % всех ядер", s => s.AppsCpu, "F2");
        Row("CPU ElasticMem, % всех ядер", s => s.AgentCpu, "F2");
        Row("Агент вытеснил, МБ", s => M(s.AgentTrimmed));
        sb.AppendLine();
        var a = all.Where(s => s.Phase == Phase.A).ToList();
        if (a.Count > 0)
            foreach (var p in phases.Where(p => p != Phase.A))
            {
                var x = all.Where(s => s.Phase == p).ToList();
                double dRam = M(a.Average(s => s.InUse) - x.Average(s => s.InUse));
                double dDisk = x.Average(s => s.DiskReadMBps) - a.Average(s => s.DiskReadMBps);
                double dUi = x.Average(s => s.UiP95) - a.Average(s => s.UiP95);
                sb.AppendLine($"{Name(p)} против A: занятая RAM {(dRam >= 0 ? "меньше" : "больше")} на {Math.Abs(dRam):F0} МБ; " +
                              $"чтение с диска {(dDisk >= 0 ? "+" : "")}{dDisk:F2} МБ/с; отклик p95 {(dUi >= 0 ? "+" : "")}{dUi:F1} мс.");
            }
        sb.AppendLine();
        sb.AppendLine("Как читать: экономия = разница «Занятой RAM системы» (это то, что реально освободилось для других программ),");
        sb.AppendLine("а не сумма уменьшений рабочих наборов. Рост «чтения страниц с диска» и «записи изменённых» = вытеснение ушло на диск, а не в сжатие.");
        sb.AppendLine("Сравнение честно только при одинаковом сценарии; разница меньше ~150 МБ — в пределах шума. Лучше 2 круга.");
        sb.AppendLine();
        sb.AppendLine("По фазам:");
        foreach (var s in all)
            sb.AppendLine($"  {Name(s.Phase)}, круг {s.Round}: {s.Sec / 60:F1} мин, занято {M(s.InUse0):F0} → {M(s.InUse1):F0} МБ, хранилище {M(s.Store0):F0} → {M(s.Store1):F0} МБ, окон-проб {s.UiSamples}");
        sb.AppendLine("Журнал агента:");
        lock (log) foreach (var l in log.TakeLast(200)) sb.AppendLine("  " + l);
        return sb.ToString();
    }
}
