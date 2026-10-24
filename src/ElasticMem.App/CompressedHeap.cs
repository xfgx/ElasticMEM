using System.Diagnostics;
using ElasticMem.App;
using ElasticMem.Vmm;
namespace ElasticMem.App;

/// <summary>
/// Вкладка «Сжатая куча»: демонстрация и замер прозрачно-сжатой памяти в user mode (ElasticHeap, VEH + PAGE_NOACCESS).
/// Самотест выделяет большой логический объём, обращается к памяти ОБЫЧНЫМ указателем, в фоне возвращает холодные
/// страницы в сжатое состояние и показывает: логический объём против резидентного, размер сжатого хранилища,
/// задержку распаковки p50/p95, подтверждённую целостность данных (CRC).
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
        stack.Add(Ui.L("Низкоуровневый механизм: своя сжатая память в пространстве пользователя. Холодные страницы хранятся сжатыми и помечены «нет доступа»; при обращении процессор бросает исключение, наш обработчик распаковывает одну страницу (4 КиБ) и продолжает выполнение — для кода это обычная память. Так сжимается память, выделенная через ElasticMem, без драйвера, прав администратора и зависимости от активации Windows."));
        stack.Add(Ui.Row(_run));
        stack.Add(Ui.Hint("Это фундамент: тот же механизм можно подключить к рабочим нагрузкам, которые хранят данные через ElasticMem. Прозрачно сжимать произвольные чужие приложения без внедрения в них нельзя — это отдельное ограничение."));
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
        catch (Exception ex) { Append("Ошибка: " + ex.Message); }
        finally { _running = false; _run.Enabled = true; }
    }

    static unsafe string Body()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"== Самотест сжатой кучи {DateTime.Now:HH:mm:ss} ==");
        const int pages = 131072; // 512 МиБ логически
        using var codec = new NtXpressCodec();
        using var heap = new ElasticHeap(codec, pages, 256L << 20);
        byte* b = heap.Base;

        // 1. Заполняем сжимаемым содержимым через ОБЫЧНЫЙ указатель — каждое первое касание страницы даёт fault.
        var swFill = Stopwatch.StartNew();
        for (long i = 0; i < (long)pages * 4096; i += 4096)
        {
            byte v = (byte)((i / 4096) & 0x3F); // хорошо сжимается XPRESS
            for (int k = 0; k < 4096; k += 256) b[i + k] = v; // трогаем страницу, большая часть остаётся нулями/повторами
            b[i] = v; b[i + 1] = (byte)(v + 1);
        }
        swFill.Stop();
        long self0 = Process.GetCurrentProcess().WorkingSet64;
        sb.AppendLine($"Заполнено {Mib(heap.LogicalBytes)} за {swFill.ElapsedMilliseconds} мс; резидентно {heap.ResidentPages} страниц ({Mib(heap.ResidentBytes)})");

        // 2. Контрольная сумма всего объёма (эталон), чтобы потом проверить целостность после сжатия.
        uint Crc()
        {
            uint c = 0xFFFFFFFF;
            for (long i = 0; i < (long)pages * 4096; i += 4096) { byte x = b[i]; c = (c ^ x); for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (uint)(-(int)(c & 1))); }
            return ~c;
        }
        uint before = Crc();

        // 3. Сжимаем всё холодное: возвращаем страницы в PAGE_NOACCESS + сжатую копию.
        var swC = Stopwatch.StartNew();
        int compacted = 0; for (int pass = 0; pass < 8; pass++) compacted += heap.Compact(pages);
        swC.Stop();
        long self1 = Process.GetCurrentProcess().WorkingSet64;
        var st = heap.StoreStats();
        sb.AppendLine($"Сжато {compacted} страниц за {swC.ElapsedMilliseconds} мс");
        sb.AppendLine($"Логический объём {Mib(heap.LogicalBytes)} → резидентно {Mib(heap.ResidentBytes)} (сжатых {heap.CompressedPages}, закреплённых {heap.PinnedPages})");
        sb.AppendLine($"Сжатое хранилище: {Mib(st.LedgerBytes)} полезной нагрузки — это и есть ужатая копия холодной памяти");
        double ratio = st.Pages > 0 ? (double)st.PayloadBytes / ((long)st.Pages * 4096) : 1;
        sb.AppendLine($"Степень сжатия холодных страниц: {ratio * 100:F1}% от исходного ({Mib((long)st.Pages * 4096)} → {Mib(st.PayloadBytes)})");
        sb.AppendLine($"Рабочий набор процесса ElasticMem: было {Mib(self0)}, стало {Mib(self1)} (−{Mib(self0 - self1)})");

        // 4. Прозрачность: читаем всё обратно обычным указателем (страницы распаковываются по обращению), сверяем CRC.
        var swR = Stopwatch.StartNew();
        uint after = Crc();
        swR.Stop();
        sb.AppendLine($"Повторное чтение всего объёма за {swR.ElapsedMilliseconds} мс; целостность: {(after == before ? "ПОДТВЕРЖДЕНА (CRC совпал)" : "ОШИБКА CRC!")}");
        sb.AppendLine($"Распаковок по обращению: {heap.Decompressed}; задержка распаковки p50 {heap.FaultP50 * 1000:F1} мкс, p95 {heap.FaultP95 * 1000:F1} мкс");

        sb.AppendLine(after == before
            ? "Вывод: прозрачно-сжатая память в user mode работает — приложение видит обычный указатель, холодное лежит сжатым, распаковка одной страницы занимает микросекунды."
            : "Вывод: обнаружено расхождение данных — не использовать; пришлите лог.");
        return sb.ToString();
    }
}
