using System.Runtime.InteropServices;

namespace ElasticMem.App;

/// <summary>Чтение КОПИЙ страниц приватной зафиксированной читаемой памяти процесса. Только чтение.</summary>
internal static class PageSampler
{
    public static List<byte[]>? Sample(int pid, int max, out string? error)
    {
        error = null;
        var h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) { error = "OpenProcess Win32 " + Marshal.GetLastWin32Error(); return null; }
        try
        {
            var addrs = new List<long>(); long addr = 0; int mbiSize = Marshal.SizeOf<Native.MEMORY_BASIC_INFORMATION>();
            while (addrs.Count < 2_000_000 && Native.VirtualQueryEx(h, (IntPtr)addr, out var mbi, (IntPtr)mbiSize) != IntPtr.Zero)
            {
                long size = (long)(ulong)mbi.RegionSize;
                if (mbi.State == Native.MEM_COMMIT && mbi.Type == Native.MEM_PRIVATE && (mbi.Protect & Native.ReadableMask) != 0 && (mbi.Protect & Native.PAGE_GUARD) == 0)
                    for (long o = 0; o < size; o += 4096) addrs.Add((long)mbi.BaseAddress + o);
                long next = (long)mbi.BaseAddress + size; if (next <= addr) break; addr = next;
            }
            var rng = new Random(pid);
            for (int i = addrs.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (addrs[i], addrs[j]) = (addrs[j], addrs[i]); }
            var pages = new List<byte[]>();
            foreach (long a in addrs)
            {
                if (pages.Count >= max) break;
                var buf = new byte[4096];
                if (Native.ReadProcessMemory(h, (IntPtr)a, buf, (IntPtr)4096, out var r) && (long)r == 4096) pages.Add(buf);
            }
            if (pages.Count == 0) { error = "ни одна страница не прочиталась"; return null; }
            return pages;
        }
        finally { Native.CloseHandle(h); }
    }
}
