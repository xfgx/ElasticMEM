using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ElasticMem.Core.Access;

namespace ElasticMem.App;

/// <summary>
/// Тест приложения целиком (все его процессы): единый анализ памяти, изменчивость за N секунд,
/// и (по желанию) реальный тест сжатия средствами Windows с измерением возврата страниц.
/// </summary>
public static class ProfileScanner
{
    public sealed class Options
    {
        public long TotalLimitBytes = 512L << 20;
        public long PerProcessLimitBytes = 256L << 20;
        public long RelationBudgetBytes = 48L << 20;
        public double RecheckSec = 10;
        public bool RunTrimTest;
        public double TrimObserveSec = 30;
    }

    private sealed class ProcInfo
    {
        public int Pid; public string Name = ""; public long Ws, Private, Scanned; public int Regions;
        public List<(long addr, ulong hash)> Pages = new();
        public string Error = "";
    }

    public static string Run(AppProfile prof, Options o, IProgress<string> progress, CancellationToken ct)
    {
        var procs = prof.Find();
        if (procs.Count == 0) return $"{prof.Title}: процессы не найдены. Запустите приложение и нажмите «Обновить».";
        var infos = new List<ProcInfo>();
        foreach (var p in procs)
        {
            try { infos.Add(new ProcInfo { Pid = p.Id, Name = p.ProcessName, Ws = p.WorkingSet64, Private = p.PrivateMemorySize64 }); }
            catch { }
            p.Dispose();
        }
        infos = infos.OrderByDescending(i => i.Ws).ToList();
        var insight = new MemoryInsight(o.RelationBudgetBytes);
        var sw = Stopwatch.StartNew();
        long total = 0;
        var chunk = new byte[1 << 20];
        var page = new byte[4096];

        // Квоты пропорционально рабочему набору: иначе весь лимит съедают 1–2 самых больших процесса (так было у Chrome).
        long sumWs = Math.Max(1, infos.Sum(i => Math.Max(i.Ws, 1L << 20)));
        foreach (var pi in infos)
        {
            ct.ThrowIfCancellationRequested();
            if (total >= o.TotalLimitBytes) break;
            long quota = Math.Clamp((long)((double)o.TotalLimitBytes * Math.Max(pi.Ws, 1L << 20) / sumWs), 16L << 20, o.PerProcessLimitBytes);
            var h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_VM_READ, false, pi.Pid);
            if (h == IntPtr.Zero) { pi.Error = $"нет доступа (код {Marshal.GetLastWin32Error()}) — запустите от администратора"; continue; }
            try
            {
                long addr = 0, mine = 0;
                int mbiSize = Marshal.SizeOf<Native.MEMORY_BASIC_INFORMATION>();
                while (mine < quota && total < o.TotalLimitBytes &&
                       Native.VirtualQueryEx(h, (IntPtr)addr, out var mbi, (IntPtr)mbiSize) != IntPtr.Zero)
                {
                    long size = (long)(ulong)mbi.RegionSize;
                    if (mbi.State == Native.MEM_COMMIT && mbi.Type == Native.MEM_PRIVATE &&
                        (mbi.Protect & Native.ReadableMask) != 0 && (mbi.Protect & Native.PAGE_GUARD) == 0)
                    {
                        pi.Regions++;
                        long ra = (long)mbi.BaseAddress;
                        for (long off = 0; off < size && mine < quota && total < o.TotalLimitBytes; off += chunk.Length)
                        {
                            ct.ThrowIfCancellationRequested();
                            int len = (int)Math.Min(chunk.Length, Math.Min(size - off, quota - mine));
                            len &= ~4095;
                            if (len == 0) break;
                            var valid = ReadAvailable(h, ra + off, chunk, len, page);
                            foreach (int p in valid)
                            {
                                var pg = chunk.AsSpan(p, 4096);
                                insight.Feed(pg);
                                pi.Pages.Add((ra + off + p, Fnv(pg)));
                            }
                            long got = valid.Count * 4096L;
                            mine += got; total += got; pi.Scanned += got;
                            progress.Report($"{pi.Name} ({pi.Pid}): {mine >> 20} МБ, всего {total >> 20} МБ");
                        }
                    }
                    long next = (long)mbi.BaseAddress + size;
                    if (next <= addr) break;
                    addr = next;
                }
            }
            finally { Native.CloseHandle(h); }
        }
        double scanSec = sw.Elapsed.TotalSeconds;
        var rep = insight.Report();

        // изменчивость: сколько страниц изменилось за N секунд
        long pagesTotal = infos.Sum(i => i.Pages.Count), changed = 0, gone = 0;
        if (o.RecheckSec > 0 && pagesTotal > 0)
        {
            for (double t = o.RecheckSec; t > 0; t -= 1) { ct.ThrowIfCancellationRequested(); progress.Report($"Ожидание изменений: {t:F0} с"); Thread.Sleep(1000); }
            foreach (var pi in infos.Where(i => i.Pages.Count > 0))
            {
                var h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_VM_READ, false, pi.Pid);
                if (h == IntPtr.Zero) { gone += pi.Pages.Count; continue; }
                try
                {
                    int k = 0;
                    foreach (var (a, hh) in pi.Pages)
                    {
                        if ((++k & 4095) == 0) { ct.ThrowIfCancellationRequested(); progress.Report($"Повторный снимок {pi.Name}: {k}/{pi.Pages.Count}"); }
                        if (!Native.ReadProcessMemory(h, (IntPtr)a, page, (IntPtr)4096, out var r) || (long)r != 4096) { gone++; continue; }
                        if (Fnv(page) != hh) changed++;
                    }
                }
                finally { Native.CloseHandle(h); }
            }
        }

        string trim = o.RunTrimTest ? TrimTest(prof, o.TrimObserveSec, progress, ct) : "";

        var sb = new StringBuilder();
        sb.AppendLine($"══ {prof.Title} — {DateTime.Now:yyyy-MM-dd HH:mm} ══");
        if (prof.AntiCheatRisk) sb.AppendLine("Внимание: игра с античитом — см. примечание профиля.");
        sb.AppendLine($"Процессов: {infos.Count}; рабочий набор всего {infos.Sum(i => i.Ws) >> 20} МБ, приватной памяти {infos.Sum(i => i.Private) >> 20} МБ; скан {scanSec:F0} с");
        foreach (var pi in infos.Take(12))
            sb.AppendLine($"  {pi.Name,-22} PID {pi.Pid,-6} РН {pi.Ws >> 20,5} МБ   просканировано {pi.Scanned >> 20,4} МБ ({pi.Regions} регионов) {pi.Error}");
        if (infos.Count > 12) sb.AppendLine($"  … и ещё {infos.Count - 12}");
        sb.AppendLine();
        sb.Append(MemoryInsight.Format("Анализ содержимого памяти:", rep));
        if (o.RecheckSec > 0 && pagesTotal > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"ИЗМЕНЧИВОСТЬ за {o.RecheckSec:F0} с: изменилось {100.0 * changed / pagesTotal:F1}% страниц, освобождено/недоступно {100.0 * gone / pagesTotal:F1}%");
            sb.AppendLine(changed * 10 < pagesTotal ? "  → большая часть памяти «холодная»: подходит сжатие в паузах (агент) и сжатая основа."
                                                    : "  → много активно меняющихся страниц: сжимать их невыгодно, только холодную часть.");
        }
        if (trim != "") { sb.AppendLine(); sb.Append(trim); }
        return sb.ToString();
    }

    /// <summary>
    /// Реальный тест: отдать рабочие наборы процессов приложения Windows (Memory Compression) и посмотреть возврат.
    /// v0.5: GPU/аудио-процессы пропускаются (как в агенте); возврат считается точно — по выборке собственных страниц,
    /// отдельно от новых выделений памяти.
    /// </summary>
    public static string TrimTest(AppProfile prof, double observeSec, IProgress<string> progress, CancellationToken ct)
    {
        var procs = prof.Find();
        var hs = new List<(Process p, IntPtr h, long ws0, uint pf0, WsProbe? probe, ProcRole role)>();
        var skipped = new List<string>();
        long store0 = IdleTrimAgent.CompressionStoreBytes();
        var mem0 = Native.MemoryStatus();
        foreach (var p in procs)
        {
            var h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_VM_READ | Native.PROCESS_SET_QUOTA, false, p.Id);
            if (h == IntPtr.Zero) h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_QUOTA, false, p.Id);
            if (h == IntPtr.Zero) { p.Dispose(); continue; }
            var role = ProcRoles.Classify(Native.CommandLine(h));
            Native.TryGetMemInfo(h, out var mi);
            if (ProcRoles.NeverTrim(role)) { skipped.Add($"{p.ProcessName} {p.Id} ({ProcRoles.Ru(role)}, {mi.WorkingSetSize >> 20} МБ)"); Native.CloseHandle(h); p.Dispose(); continue; }
            progress.Report($"Выборка страниц {p.ProcessName} ({p.Id})");
            hs.Add((p, h, (long)mi.WorkingSetSize, mi.PageFaultCount, WsProbe.Capture(h, 16384, p.Id), role));
        }
        foreach (var x in hs) Native.K32EmptyWorkingSet(x.h);
        Thread.Sleep(500);
        long wsAfter = 0;
        var after = new long[hs.Count]; var r0 = new double[hs.Count];
        for (int i = 0; i < hs.Count; i++)
        {
            Native.TryGetMemInfo(hs[i].h, out var mi); after[i] = (long)mi.WorkingSetSize; wsAfter += after[i];
            r0[i] = hs[i].probe?.ResidentFraction(hs[i].h) ?? -1;
        }
        long ws0 = hs.Sum(x => x.ws0);
        long freed = ws0 - wsAfter;
        long store1 = IdleTrimAgent.CompressionStoreBytes();
        var mem1 = Native.MemoryStatus();
        for (double t = observeSec; t > 0; t -= 1) { ct.ThrowIfCancellationRequested(); progress.Report($"Наблюдение после сжатия: {t:F0} с (пользуйтесь приложением как обычно)"); Thread.Sleep(1000); }
        long wsEnd = 0, faults = 0; double retBytes = 0, measBytes = 0;
        var perProc = new List<string>();
        for (int i = 0; i < hs.Count; i++)
        {
            var x = hs[i];
            if (Native.TryGetMemInfo(x.h, out var mi))
            {
                long end = (long)mi.WorkingSetSize; wsEnd += end; faults += mi.PageFaultCount - x.pf0;
                long fr = Math.Max(0, x.ws0 - after[i]);
                if (x.probe != null && r0[i] >= 0 && r0[i] < 0.98)
                {
                    double r1 = x.probe.ResidentFraction(x.h);
                    if (r1 >= 0)
                    {
                        double ret = Math.Clamp((r1 - r0[i]) / (1 - r0[i]), 0, 1);
                        retBytes += ret * fr; measBytes += fr;
                        if (fr > (16L << 20)) perProc.Add($"    {x.p.ProcessName,-16} {x.p.Id,-6} {ProcRoles.Ru(x.role),-12} {x.ws0 >> 20,5} → {after[i] >> 20,4} → {end >> 20,5} МБ, вернулось {ret * 100,3:F0}% сжатых");
                    }
                }
            }
            Native.CloseHandle(x.h); x.p.Dispose();
        }
        long store2 = IdleTrimAgent.CompressionStoreBytes();
        var mem2 = Native.MemoryStatus();
        double retPct = measBytes > 0 ? 100 * retBytes / measBytes : (freed > 0 ? 100.0 * Math.Max(0, wsEnd - wsAfter) / freed : 0);
        long returned = (long)(freed * retPct / 100);
        long fresh = Math.Max(0, wsEnd - wsAfter - returned);
        var sb = new StringBuilder();
        sb.AppendLine("РЕАЛЬНЫЙ ТЕСТ СЖАТИЯ (Windows Memory Compression, без потерь):");
        if (skipped.Count > 0) sb.AppendLine($"  Не трогали (как и агент): {string.Join("; ", skipped)}");
        sb.AppendLine($"  Рабочий набор: {ws0 >> 20} → {wsAfter >> 20} МБ сразу (освобождено {freed >> 20} МБ) → {wsEnd >> 20} МБ через {observeSec:F0} с");
        sb.AppendLine($"  Вернулось сжатых страниц: {retPct:F0}% ({(measBytes > 0 ? "точно, по выборке страниц" : "оценка по рабочему набору")}); ошибок страниц: {faults}");
        sb.AppendLine($"  Новые выделения памяти за время наблюдения (не возврат): ≈{fresh >> 20} МБ");
        foreach (var l in perProc.Take(10)) sb.AppendLine(l);
        if (store0 >= 0) sb.AppendLine($"  Хранилище сжатой памяти Windows: {store0 >> 20} → {store1 >> 20} → {store2 >> 20} МБ");
        sb.AppendLine($"  Свободная RAM системы: {mem0.ullAvailPhys >> 20} → {mem1.ullAvailPhys >> 20} → {mem2.ullAvailPhys >> 20} МБ");
        long kept = freed - returned;
        sb.AppendLine(retPct < 35 && kept > (64L << 20) ? $"  → устойчиво освобождено ≈{kept >> 20} МБ: агент для этого приложения полезен."
                    : retPct < 35 ? "  → возврат мал, но и памяти освобождено немного."
                                  : "  → много страниц вернулось: приложение активно использует память — сжимать только в простое.");
        return sb.ToString();
    }

    /// <summary>Читает диапазон; возвращает смещения успешно прочитанных страниц (недоступные пропускаются).</summary>
    private static List<int> ReadAvailable(IntPtr h, long addr, byte[] buf, int len, byte[] page)
    {
        var res = new List<int>(len / 4096);
        if (Native.ReadProcessMemory(h, (IntPtr)addr, buf, (IntPtr)len, out var r) && (long)r == len)
        {
            for (int p = 0; p < len; p += 4096) res.Add(p);
            return res;
        }
        for (int p = 0; p < len; p += 4096)
        {
            if (!Native.ReadProcessMemory(h, (IntPtr)(addr + p), page, (IntPtr)4096, out r) || (long)r != 4096) continue;
            Buffer.BlockCopy(page, 0, buf, p, 4096);
            res.Add(p);
        }
        return res;
    }

    private static ulong Fnv(ReadOnlySpan<byte> s)
    {
        ulong h = 14695981039346656037UL;
        foreach (var w in MemoryMarshal.Cast<byte, ulong>(s)) { h ^= w; h *= 1099511628211UL; }
        return h;
    }
}
