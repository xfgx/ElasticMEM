using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ElasticMem.App;

/// <summary>
/// Учёт памяти всей системы (а не сумма рабочих наборов): списки страниц Windows (изменённые, резервные по приоритетам,
/// свободные), коммит, хранилище сжатия, страничные ошибки по типам, чтение/запись страниц с диска.
/// </summary>
public static class SysMem
{
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr tok);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool LookupPrivilegeValue(string? sys, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(IntPtr tok, bool disableAll, ref TOKEN_PRIVILEGES np, int len, IntPtr prev, IntPtr retLen);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct TOKEN_PRIVILEGES { public int Count; public long Luid; public int Attr; }

    private static bool _priv;
    private static void EnsurePrivilege()
    {
        if (_priv) return; _priv = true;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), 0x20 | 0x8, out var tok)) return;
            if (LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out long luid))
            {
                var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attr = 2 };
                AdjustTokenPrivileges(tok, false, ref tp, Marshal.SizeOf<TOKEN_PRIVILEGES>(), IntPtr.Zero, IntPtr.Zero);
            }
            Native.CloseHandle(tok);
        }
        catch { }
    }

    public struct Snap
    {
        public DateTime T;
        public long Total, Available, Commit, CommitLimit, Store;
        public long Modified, Standby, StandbyLow, FreeZero; public bool HasLists;
        public uint PageFaults, Transition, DemandZero, PageReads, PageReadIos, DirtyWrites, DirtyWriteIos; public bool HasPerf;
        public long[] StandbyByPrio;
    }

    public static Snap Take()
    {
        EnsurePrivilege();
        var m = Native.MemoryStatus();
        var s = new Snap
        {
            T = DateTime.UtcNow, Total = (long)m.ullTotalPhys, Available = (long)m.ullAvailPhys,
            Commit = (long)(m.ullTotalPageFile - m.ullAvailPageFile), CommitLimit = (long)m.ullTotalPageFile,
            Store = IdleTrimAgent.CompressionStoreBytes(), StandbyByPrio = new long[8],
        };
        // SystemMemoryListInformation (80): нужны права администратора (SeProfileSingleProcessPrivilege)
        IntPtr buf = Marshal.AllocHGlobal(1024);
        try
        {
            int ps = IntPtr.Size;
            int len = ps * (5 + 8 + 8 + 1);
            if (NtQuerySystemInformation(80, buf, len, out _) == 0)
            {
                long P(int i) => (long)Marshal.ReadIntPtr(buf, i * ps) * 4096;
                s.FreeZero = P(0) + P(1); s.Modified = P(2) + P(3);
                for (int i = 0; i < 8; i++) { s.StandbyByPrio[i] = P(5 + i); s.Standby += s.StandbyByPrio[i]; }
                s.StandbyLow = s.StandbyByPrio[0] + s.StandbyByPrio[1] + s.StandbyByPrio[2];
                s.HasLists = true;
            }
            // SystemPerformanceInformation (2): 32-битные счётчики, считаем разности
            for (int sz = 0x158; sz <= 0x3F8 && !s.HasPerf; sz += 8)
            {
                if (NtQuerySystemInformation(2, buf, sz, out _) != 0) continue;
                uint U(int off) => (uint)Marshal.ReadInt32(buf, off);
                s.PageFaults = U(60); s.Transition = U(68); s.DemandZero = U(76);
                s.PageReads = U(80); s.PageReadIos = U(84); s.DirtyWrites = U(96); s.DirtyWriteIos = U(100);
                s.HasPerf = true;
            }
        }
        catch { }
        finally { Marshal.FreeHGlobal(buf); }
        return s;
    }
}

/// <summary>
/// Отзывчивость окон приложения: WM_NULL через SendMessageTimeout в главные окна процессов.
/// Если поток окна стоит (в т. ч. на страничных ошибках), задержка видна сразу. Это прокси «задержек пользовательских операций».
/// </summary>
public static class UiProbe
{
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr res);

    public static List<IntPtr> Windows(HashSet<int> pids)
    {
        var res = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && GetWindow(h, 4 /*GW_OWNER*/) == IntPtr.Zero)
            {
                Native.GetWindowThreadProcessId(h, out int pid);
                if (pids.Contains(pid)) res.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        return res;
    }

    /// <summary>Задержка ответа окна, мс (1000 = не ответило за секунду).</summary>
    public static double Ping(IntPtr h)
    {
        var sw = Stopwatch.StartNew();
        var r = SendMessageTimeout(h, 0 /*WM_NULL*/, IntPtr.Zero, IntPtr.Zero, 0x2 /*SMTO_ABORTIFHUNG*/, 1000, out _);
        return r == IntPtr.Zero ? 1000 : sw.Elapsed.TotalMilliseconds;
    }
}
