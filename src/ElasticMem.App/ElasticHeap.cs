using System.Runtime.InteropServices;
using ElasticMem.Core;
using ElasticMem.Core.Backend.Vmm;
namespace ElasticMem.App;

/// <summary>
/// Прозрачно-сжатая куча в user mode: «userfaultfd для Windows». Резервируем диапазон адресов, холодные
/// страницы держим PAGE_NOACCESS и в сжатом виде. При обращении CPU бросает access violation, векторный
/// обработчик (VEH) распаковывает ровно одну 4-КиБ страницу, ставит PAGE_READWRITE и возобновляет инструкцию.
/// Фоновый поток возвращает холодные страницы в сжатое состояние. Без драйвера, прав администратора и
/// активации Windows. Сжимается ТОЛЬКО память, выделенная через эту кучу (данные самого приложения/движка),
/// чужие процессы не трогаются.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed unsafe class ElasticHeap : IDisposable
{
    const int PageBytes = 4096;
    const uint MEM_RESERVE = 0x2000, MEM_COMMIT = 0x1000, MEM_RELEASE = 0x8000;
    const uint PAGE_NOACCESS = 0x01, PAGE_READWRITE = 0x04;
    const int EXCEPTION_CONTINUE_EXECUTION = -1, EXCEPTION_CONTINUE_SEARCH = 0;

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAlloc(IntPtr addr, nuint size, uint type, uint prot);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualFree(IntPtr addr, nuint size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualProtect(IntPtr addr, nuint size, uint prot, out uint old);
    [DllImport("kernel32.dll")] static extern IntPtr AddVectoredExceptionHandler(uint first, VectoredHandler handler);
    [DllImport("kernel32.dll")] static extern uint RemoveVectoredExceptionHandler(IntPtr handle);

    delegate int VectoredHandler(IntPtr exceptionInfo);

    [StructLayout(LayoutKind.Sequential)] struct EXCEPTION_RECORD { public uint Code, Flags; public IntPtr Record, Address; public uint NumberParameters; }

    readonly byte* _base;
    readonly int _pages;
    readonly CompressedPageTable _table;
    readonly object _gate = new();
    readonly VectoredHandler _handler; // держим ссылку, чтобы делегат не собрал GC
    readonly IntPtr _veh;
    public static bool AllowExperimentalVeh;
    volatile bool _disposed;
    long _faults, _decompressed, _evictions; double _faultMsTotal; readonly List<double> _faultMs = new();

    public ElasticHeap(IPageCodec codec, int pages, long quotaBytes)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        // 0.36.0: запуск из GUI ронял процесс (управляемые выделения/вызовы внутри VEH). Путь не готов —
        // оставлен в исходниках для доработки, но включается только явным флагом, не из приложения.
        if (!AllowExperimentalVeh) throw new NotSupportedException("Транзакционный VEH-путь временно отключён как небезопасный; используйте CompressedPageTable напрямую.");
        if (pages is < 1 or > 1048576) throw new ArgumentOutOfRangeException(nameof(pages));
        _pages = pages; _table = new CompressedPageTable(codec, pages, quotaBytes);
        IntPtr region = VirtualAlloc(IntPtr.Zero, (nuint)((long)pages * PageBytes), MEM_RESERVE, PAGE_NOACCESS);
        if (region == IntPtr.Zero) { _table.Dispose(); throw new OutOfMemoryException("VirtualAlloc reserve failed"); }
        _base = (byte*)region;
        _handler = Handler;
        _veh = AddVectoredExceptionHandler(1, _handler);
        if (_veh == IntPtr.Zero) { VirtualFree(region, 0, MEM_RELEASE); _table.Dispose(); throw new InvalidOperationException("AddVectoredExceptionHandler failed"); }
    }

    public byte* Base => _base;
    public long LogicalBytes => _table.LogicalBytes;
    public long ResidentBytes => _table.ResidentBytes;
    public int ResidentPages => _table.Resident;
    public int CompressedPages => _table.Compressed;
    public int PinnedPages => _table.Pinned;
    public long Faults => _faults;
    public long Decompressed => _decompressed;
    public long Evictions => _evictions;
    public UserStoreStats StoreStats() => _table.StoreStats();
    public double FaultP50 { get { lock (_gate) { if (_faultMs.Count == 0) return 0; var a = _faultMs.ToArray(); Array.Sort(a); return a[a.Length / 2]; } } }
    public double FaultP95 { get { lock (_gate) { if (_faultMs.Count == 0) return 0; var a = _faultMs.ToArray(); Array.Sort(a); return a[Math.Min(a.Length - 1, (int)(a.Length * 0.95))]; } } }

    int PageOf(byte* addr) => addr < _base || addr >= _base + (long)_pages * PageBytes ? -1 : (int)(((long)(addr - _base)) / PageBytes);

    int Handler(IntPtr info)
    {
        // info -> EXCEPTION_POINTERS { PEXCEPTION_RECORD, PCONTEXT }. Нужен только адрес из записи.
        // EXCEPTION_POINTERS.ExceptionRecord -> EXCEPTION_RECORD (x64 layout):
        //   0 ExceptionCode(DWORD), 4 ExceptionFlags, 8 ExceptionRecord*, 16 ExceptionAddress*,
        //   24 NumberParameters(DWORD)+padding, 32 ExceptionInformation[0], 40 ExceptionInformation[1].
        IntPtr recPtr = Marshal.ReadIntPtr(info);
        var code = (uint)Marshal.ReadInt32(recPtr, 0);
        if (code != 0xC0000005) return EXCEPTION_CONTINUE_SEARCH;        // не access violation
        if (Marshal.ReadInt32(recPtr, 24) < 2) return EXCEPTION_CONTINUE_SEARCH;
        IntPtr accessed = Marshal.ReadIntPtr(recPtr, 40);               // ExceptionInformation[1] = адрес обращения
        int page = PageOf((byte*)accessed);
        if (page < 0) return EXCEPTION_CONTINUE_SEARCH; // не наша куча — пусть разбирается кто-то другой
        var sw = System.Diagnostics.Stopwatch.StartNew();
        lock (_gate)
        {
            if (_disposed) return EXCEPTION_CONTINUE_SEARCH;
            var st = _table.State(page);
            if (st is CompressedPageTable.PageState.Resident or CompressedPageTable.PageState.Pinned)
                return EXCEPTION_CONTINUE_EXECUTION; // другой поток уже вернул страницу
            byte* pageVa = _base + (long)page * PageBytes;
            if (VirtualAlloc((IntPtr)pageVa, PageBytes, MEM_COMMIT, PAGE_READWRITE) == IntPtr.Zero)
                return EXCEPTION_CONTINUE_SEARCH;
            var span = new Span<byte>(pageVa, PageBytes);
            bool decompressed = _table.OnFault(page, span);
            if (decompressed) _decompressed++;
            _faults++;
        }
        sw.Stop();
        lock (_gate) { double ms = sw.Elapsed.TotalMilliseconds; _faultMsTotal += ms; if (_faultMs.Count < 100000) _faultMs.Add(ms); }
        return EXCEPTION_CONTINUE_EXECUTION;
    }

    /// <summary>Вернуть в сжатое состояние до maxPages самых холодных резидентных страниц. Вызывать из фонового потока.</summary>
    public int Compact(int maxPages)
    {
        int done = 0;
        foreach (int page in _table.ChooseCold(maxPages))
        {
            lock (_gate)
            {
                if (_disposed) break;
                if (_table.State(page) != CompressedPageTable.PageState.Resident) continue;
                byte* pageVa = _base + (long)page * PageBytes;
                int stored = _table.Evict(page, new ReadOnlySpan<byte>(pageVa, PageBytes));
                var st = _table.State(page);
                if (st is CompressedPageTable.PageState.Compressed or CompressedPageTable.PageState.Zero)
                {
                    // Освобождаем физику: декоммитим страницу и ставим PAGE_NOACCESS, чтобы следующее обращение дало fault.
                    VirtualFree((IntPtr)pageVa, PageBytes, 0x4000 /*MEM_DECOMMIT*/);
                    _evictions++; done++;
                }
            }
        }
        return done;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            if (_veh != IntPtr.Zero) RemoveVectoredExceptionHandler(_veh);
            if (_base != null) VirtualFree((IntPtr)_base, 0, MEM_RELEASE);
            _table.Dispose();
        }
    }
}
