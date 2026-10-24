using System.Diagnostics;
using ElasticMem.Core;
using ElasticMem.Vmm;
using ElasticMem.Core.Research;
using System.Runtime.InteropServices;
namespace ElasticMem.App;

/// <summary>
/// Вкладка «Сжатая куча»: безопасный нативный замер движка прозрачно-сжатой памяти (CompressedPageTable + ntdll XPRESS).
/// Прогоняет 512 МиБ через настоящее хранилище сжатых страниц в НАТИВНОЙ памяти процесса и показывает степень
/// сжатия, экономию, задержку распаковки одной страницы (p50/p95) и подтверждает целостность (CRC).
///
/// ВАЖНО: прозрачный путь через векторный обработчик отказов (ElasticHeap, VEH + PAGE_NOACCESS) в 0.36.0 ронял
/// процесс — внутри обработчика нельзя делать управляемые выделения и вызовы. Здесь он НЕ используется; доступ к
/// страницам идёт явными вызовами. Это так же нативно проверяет кодек и хранилище, но без аварийного обработчика.
/// </summary>
public sealed class CompressedHeapTab : TabPage
{
    readonly TextBox _log;
    readonly Button _run, _bake;
    readonly ComboBox _app;
    static readonly (string name, string procs)[] Apps = { ("Edge", "msedge"), ("Chrome", "chrome"), ("Telegram", "Telegram"), ("Яндекс Браузер", "browser"), ("Discord", "Discord") };
    readonly Func<bool> _busy;
    bool _running;

    public CompressedHeapTab(TextBox log, Func<bool> busy) : base("Сжатая куча")
    {
        _log = log; _busy = busy;
        _run = new Button { Text = "🧬 Самотест сжатой кучи (512 МиБ)", AutoSize = true, Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold) };
        _run.Click += async (_, _) => await RunTest();
        _app = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        foreach (var x in Apps) _app.Items.Add(x.name);
        _app.SelectedIndex = 0;
        _bake = new Button { Text = "⚖️ Сравнить кодеки на реальной памяти", AutoSize = true, Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold) };
        _bake.Click += async (_, _) => await RunBake();
        var stack = new VStack();
        stack.Add(Ui.L("Низкоуровневый механизм: своя сжатая память в пространстве пользователя. Холодные страницы хранятся сжатыми в нативной памяти процесса; доступ к странице распаковывает ровно 4 КиБ через XPRESS. Самотест прогоняет 512 МиБ и показывает степень сжатия, экономию и задержку распаковки. Без драйвера, прав администратора и зависимости от активации Windows."));
        stack.Add(Ui.Row(_run));
        stack.Add(Ui.Row(Ui.L("Приложение:"), _app, _bake));
        stack.Add(Ui.Hint("Сравнение кодеков читает КОПИИ до 8192 страниц (32 МиБ) самого большого процесса выбранного приложения, сжимает их разными способами в памяти и показывает только числа. Страницы никуда не сохраняются и не отправляются, после замера стираются. Главный вопрос: сжимает ли что-то лучше штатного XPRESS Windows."));
        stack.Add(Ui.Hint("Это безопасная проверка движка (кодек + хранилище). Полностью прозрачный путь через обработчик отказов памяти в 0.36.0 ронял приложение и временно отключён; здесь он не используется."));
        stack.Add(_log, fill: true);
        Controls.Add(stack);
    }

    void Append(string s) { if (InvokeRequired) { BeginInvoke(() => Append(s)); return; } if (_log.TextLength > 200_000) _log.Text = _log.Text[^100_000..]; _log.AppendText(s.TrimEnd() + Environment.NewLine); }
    static string Mib(long b) => (b / (1024.0 * 1024.0)).ToString("0") + " МиБ";

    async Task RunTest()
    {
        if (_running || _busy()) { Append("Занято другой операцией."); return; }
        _running = true; _run.Enabled = false;
        try { if (!OperatingSystem.IsWindows()) { Append("Только для Windows."); return; } Append(await Task.Run(Body)); }
        catch (Exception ex) { Append("Ошибка: " + ex.GetType().Name + ": " + ex.Message); }
        finally { _running = false; _run.Enabled = true; }
    }

    async Task RunBake()
    {
        if (_running || _busy()) { Append("Занято другой операцией."); return; }
        _running = true; _bake.Enabled = false; _run.Enabled = false;
        string procName = Apps[_app.SelectedIndex].procs, label = Apps[_app.SelectedIndex].name;
        try
        {
            if (!OperatingSystem.IsWindows()) { Append("Только для Windows."); return; }
            Append($"Сравнение кодеков: {label} — читаю копии страниц…");
            Append(await Task.Run(() => Bake(procName, label)));
        }
        catch (Exception ex) { Append("Ошибка: " + ex.GetType().Name + ": " + ex.Message); }
        finally { _running = false; _bake.Enabled = true; _run.Enabled = true; }
    }

    static string Bake(string procName, string label)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"== Сравнение кодеков на реальной памяти: {label} {DateTime.Now:HH:mm:ss} ==");
        // Самый большой процесс приложения (обычно вкладка/основной).
        int pid = 0; long best = 0;
        foreach (var p in Process.GetProcessesByName(procName))
        {
            try { if (p.WorkingSet64 > best) { best = p.WorkingSet64; pid = p.Id; } } catch { }
            finally { p.Dispose(); }
        }
        if (pid == 0) { sb.AppendLine($"Процесс «{procName}» не найден. Запустите приложение и откройте в нём что-нибудь."); return sb.ToString(); }
        var pages = SamplePages(pid, 8192, out string? err);
        if (pages == null) { sb.AppendLine("Не удалось прочитать память: " + err + " (для чужого пользователя нужны права администратора)."); return sb.ToString(); }
        sb.AppendLine($"Процесс PID {pid}, рабочий набор {Mib(best)}; прочитано копий страниц: {pages.Count} ({Mib((long)pages.Count * 4096)})");
        CodecBakeoff.Report r;
        try { r = CodecBakeoff.Run(pages, windowsCodecs: true); }
        finally { foreach (var pg in pages) Array.Clear(pg); pages.Clear(); } // копии стираем сразу
        if (r.Error != null) sb.AppendLine("Ошибка замера: " + r.Error);
        sb.AppendLine($"Нулевых страниц {r.ZeroPages} из {r.SampledPages} (их Windows и так не хранит — исключены). Проверочная выборка: {r.TestPages} страниц.");
        sb.AppendLine($"Точных дубликатов страниц: {r.DuplicatePages} ({Mib(r.DuplicateBytes)}) — экономия дедупликацией сверх любого кодека.");
        sb.AppendLine("Кодек                    | размер от исходного | распаковка 4 КиБ p50 / p95 | сжатие");
        foreach (var row in r.Rows)
            sb.AppendLine($"{row.Codec,-24} | {row.RatioPercent,6:F1}%             | {row.DecodeP50Us,6:F1} / {row.DecodeP95Us,6:F1} мкс       | {row.EncodeMBps,6:F0} МБ/с   {row.Note}");
        var xp = r.Rows.FirstOrDefault(x => x.Codec == "xpress 4K");
        if (xp != null && r.Rows.Length > 1)
        {
            var bestRow = r.Rows.Where(x => x.Pages > 0).MinBy(x => x.RatioPercent)!;
            double gain = xp.RatioPercent - bestRow.RatioPercent; // п.п. от исходного объёма
            sb.AppendLine();
            sb.AppendLine($"Штатный XPRESS Windows: {xp.RatioPercent:F1}%. Лучший: {bestRow.Codec} — {bestRow.RatioPercent:F1}%.");
            sb.AppendLine(gain >= 5
                ? $"Вывод: есть запас — {bestRow.Codec} хранит на {gain:F1} п.п. исходного объёма меньше, чем Windows (это ≈{gain / Math.Max(1e-9, xp.RatioPercent) * 100:F0}% от того, что занимает сжатое Windows). Своё хранилище может дать реальную экономию сверх Windows; цена — распаковка {bestRow.DecodeP50Us:F1} мкс против {xp.DecodeP50Us:F1} мкс."
                : $"Вывод: запаса почти нет ({gain:F1} п.п.) — на этих данных своё сжатие не обгоняет Windows заметно. Своё хранилище окупится только управляемостью, не экономией.");
        }
        return sb.ToString();
    }

    // Копии случайных страниц приватной зафиксированной читаемой памяти процесса. Только чтение.
    static List<byte[]>? SamplePages(int pid, int max, out string? error)
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

    static string Body()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"== Самотест сжатой кучи {DateTime.Now:HH:mm:ss} ==");
        const int pages = 131072; // 512 МиБ логически
        using var codec = new NtXpressCodec();
        using var table = new CompressedPageTable(codec, pages, 256L << 20);
        var work = new byte[4096];
        var crc = new uint[pages];
        long self0 = Process.GetCurrentProcess().WorkingSet64;

        // 1. Записываем сжимаемое содержимое и сразу вытесняем в сжатое хранилище (нативная память).
        var sw = Stopwatch.StartNew(); int pinned = 0;
        for (int p = 0; p < pages; p++)
        {
            table.OnFault(p, work);                 // Zero -> очищена, резидентна
            byte v = (byte)(p & 0x3F);
            Array.Fill(work, v);                    // хорошо сжимается
            work[0] = (byte)(v + 1); work[1] = (byte)(p & 0xFF); work[2] = (byte)((p >> 8) & 0xFF);
            crc[p] = Crc(work);
            table.Touch(p);
            table.Evict(p, work);                   // -> Compressed (или Pinned, если несжимаемо)
            if (table.State(p) == CompressedPageTable.PageState.Pinned) pinned++;
        }
        sw.Stop();
        var st = table.StoreStats();
        long self1 = Process.GetCurrentProcess().WorkingSet64;
        sb.AppendLine($"Записано и сжато {pages} страниц ({Mib(table.LogicalBytes)}) за {sw.ElapsedMilliseconds} мс");
        sb.AppendLine($"Сжатых страниц {table.Compressed}, закреплённых несжимаемых {pinned}, резидентных {table.Resident}");
        double ratio = st.Pages > 0 ? (double)st.PayloadBytes / ((long)st.Pages * 4096) : 1;
        sb.AppendLine($"Хранилище сжатых страниц: {Mib(st.LedgerBytes)} на {Mib(table.LogicalBytes)} логических — степень сжатия {ratio * 100:F1}%");
        sb.AppendLine($"Экономия относительно хранения без сжатия: {Mib(table.LogicalBytes - st.LedgerBytes)}");
        sb.AppendLine($"Рабочий набор ElasticMem: было {Mib(self0)}, стало {Mib(self1)} (держим в нативной памяти сжатую копию)");

        // 2. Читаем всё обратно (распаковка одной страницы на обращение), сверяем CRC, меряем задержку.
        var lat = new List<double>(); int bad = 0; var swR = Stopwatch.StartNew();
        var one = new byte[4096];
        for (int p = 0; p < pages; p++)
        {
            var t = Stopwatch.GetTimestamp();
            table.OnFault(p, one);                  // Compressed/Pinned -> распаковка в one
            if ((p & 7) == 0) lat.Add((Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency);
            if (Crc(one) != crc[p]) bad++;
            table.Touch(p); table.Evict(p, one);    // снова сжать, чтобы не держать всё резидентным
        }
        swR.Stop();
        lat.Sort();
        double P(double q) => lat.Count == 0 ? 0 : lat[Math.Min(lat.Count - 1, (int)(q * lat.Count))];
        sb.AppendLine($"Повторное чтение всех {pages} страниц за {swR.ElapsedMilliseconds} мс; целостность: {(bad == 0 ? "ПОДТВЕРЖДЕНА (CRC совпал для всех)" : "ОШИБКА CRC у " + bad + " страниц")}");
        sb.AppendLine($"Задержка распаковки одной страницы: p50 {P(0.5) * 1000:F1} мкс, p95 {P(0.95) * 1000:F1} мкс");
        sb.AppendLine(bad == 0
            ? "Вывод: сжатый движок работает нативно — холодная память ужимается и точно восстанавливается за микросекунды на страницу."
            : "Вывод: расхождение данных — не использовать; пришлите лог.");
        return sb.ToString();
    }

    static readonly uint[] Table = Build();
    static uint[] Build() { var t = new uint[256]; for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (uint)(-(int)(c & 1))); t[i] = c; } return t; }
    static uint Crc(byte[] b) { uint c = 0xFFFFFFFF; foreach (byte x in b) c = Table[(c ^ x) & 0xFF] ^ (c >> 8); return ~c; }
}
