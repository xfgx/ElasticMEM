using System.Runtime.InteropServices;

namespace ElasticMem.App;

internal static class Native
{
    public const uint PROCESS_QUERY_INFORMATION = 0x0400, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000,
        PROCESS_VM_READ = 0x0010, PROCESS_SET_QUOTA = 0x0100;
    public const uint MEM_COMMIT = 0x1000, MEM_PRIVATE = 0x20000, PAGE_GUARD = 0x100, PAGE_NOACCESS = 0x01;
    public const uint ReadableMask = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public UIntPtr RegionSize;
        public uint State, Protect, Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool IsWow64Process2(IntPtr h,out ushort processMachine,out ushort nativeMachine);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION mbi, IntPtr len);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool K32EmptyWorkingSet(IntPtr h);
    public const uint PROCESS_VM_OPERATION = 0x0008;
    [StructLayout(LayoutKind.Sequential)] public struct MEMORY_RANGE_ENTRY { public IntPtr VirtualAddress; public UIntPtr NumberOfBytes; }
    // Windows 8+/Server 2012+. Асинхронно подтягивает указанные диапазоны чужого процесса в рабочий набор.
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool PrefetchVirtualMemory(IntPtr h, UIntPtr numberOfEntries, [In] MEMORY_RANGE_ENTRY[] entries, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool K32GetProcessMemoryInfo(IntPtr h, out PROCESS_MEMORY_COUNTERS_EX c, uint cb);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ProcessIdToSessionId(int pid, out int session);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll")] public static extern int GetCurrentProcessId();
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);

    // --- v0.5: мягкое сжатие, приоритет памяти, точный учёт возврата страниц, роли процессов ---
    public const uint PROCESS_SET_INFORMATION = 0x0200;
    public const uint QUOTA_LIMITS_HARDWS_MIN_ENABLE = 0x1, QUOTA_LIMITS_HARDWS_MIN_DISABLE = 0x2,
        QUOTA_LIMITS_HARDWS_MAX_ENABLE = 0x4, QUOTA_LIMITS_HARDWS_MAX_DISABLE = 0x8;
    public const int ProcessMemoryPriority = 0;
    public const uint MEMORY_PRIORITY_VERY_LOW = 1, MEMORY_PRIORITY_LOW = 2, MEMORY_PRIORITY_NORMAL = 5;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessWorkingSetSizeEx(IntPtr h, UIntPtr min, UIntPtr max, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessWorkingSetSizeEx(IntPtr h, out UIntPtr min, out UIntPtr max, out uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessInformation(IntPtr h, int infoClass, ref uint info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessInformation(IntPtr h, int infoClass, out uint info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool K32QueryWorkingSet(IntPtr h, [Out] UIntPtr[] buf, uint cb);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool K32QueryWorkingSetEx(IntPtr h, [In, Out] UIntPtr[] buf, uint cb);
    [DllImport("ntdll.dll")]
    public static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buf, int len, out int retLen);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();

    public static bool SetMemoryPriority(IntPtr h, uint prio) => SetProcessInformation(h, ProcessMemoryPriority, ref prio, 4);

    /// <summary>Активное окно развёрнуто на весь монитор (игра, видео в полноэкранном режиме).</summary>
    public static bool ForegroundIsFullscreen()
    {
        var w = GetForegroundWindow();
        if (w == IntPtr.Zero || w == GetShellWindow() || w == GetDesktopWindow()) return false;
        if (!GetWindowRect(w, out var r)) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(w, 2), ref mi)) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    /// <summary>Командная строка чужого процесса (Windows 8.1+, достаточно PROCESS_QUERY_LIMITED_INFORMATION).</summary>
    public static string? CommandLine(IntPtr h)
    {
        const int ProcessCommandLineInformation = 60;
        int len = 4096;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                int st = NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, len, out int need);
                if (st == 0)
                {
                    ushort bytes = (ushort)Marshal.ReadInt16(buf);
                    IntPtr p = Marshal.ReadIntPtr(buf, IntPtr.Size); // UNICODE_STRING.Buffer
                    return p == IntPtr.Zero ? "" : Marshal.PtrToStringUni(p, bytes / 2);
                }
                if (need > len) { len = need + 16; continue; }
                return null;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return null;
    }

    public static bool TryGetMemInfo(IntPtr h, out PROCESS_MEMORY_COUNTERS_EX c)
    {
        c = default;
        c.cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>();
        return K32GetProcessMemoryInfo(h, out c, c.cb);
    }

    public static int ForegroundPid()
    {
        var w = GetForegroundWindow();
        if (w == IntPtr.Zero) return -1;
        GetWindowThreadProcessId(w, out int pid);
        return pid;
    }

    public static MEMORYSTATUSEX MemoryStatus()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref m);
        return m;
    }
}
