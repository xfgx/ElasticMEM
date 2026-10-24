using System.Diagnostics;
using ElasticMem.Core;
using ElasticMem.Vmm;
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
    readonly Button _run;
    readonly Func<bool> _busy;
    bool _running;

    public CompressedHeapTab(TextBox log, Func<bool> busy) : base("Сжатая куча")
    {
        _log = log; _busy = busy;
        _run = new Button { Text = "🧬 Самотест сжатой кучи (512 МиБ)", AutoSize = true, Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold) };
        _run.Click += async (_, _) => await RunTest();
        var stack = new VStack();
        stack.Add(Ui.L("Низкоуровневый механизм: своя сжатая память в пространстве пользователя. Холодные страницы хранятся сжатыми в нативной памяти процесса; доступ к странице распаковывает ровно 4 КиБ через XPRESS. Самотест прогоняет 512 МиБ и показывает степень сжатия, экономию и задержку распаковки. Без драйвера, прав администратора и зависимости от активации Windows."));
        stack.Add(Ui.Row(_run));
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
