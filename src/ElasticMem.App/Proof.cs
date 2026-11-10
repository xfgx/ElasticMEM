using System.Diagnostics;
using System.Runtime.InteropServices;
using ElasticMem.Core.Research;

namespace ElasticMem.App;

/// <summary>
/// Вкладка «Доказательство»: проверяет, что сжатие страниц работает ПО-НАСТОЯЩЕМУ, а не «в отчёте».
///
/// Для копий реальных страниц приложения (нулевые исключены) каждый кодек упаковывает все страницы в ОДНУ
/// арену с индексом. Арена кладётся в нативную память через VirtualAlloc, и её объём меряется СЧЁТЧИКАМИ ОС
/// (закоммиченная память процесса и рабочий набор), а не суммой длин. Затем каждая страница восстанавливается
/// из этой нативной арены и сверяется по SHA-256 с исходной; отрицательный контроль портит байт и обязан
/// это обнаружить. Все кодеки — на одних и тех же страницах, включая XPRESS, которым сжимает сама Windows.
/// </summary>
public sealed class ProofTab : TabPage
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAlloc(IntPtr addr, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualFree(IntPtr addr, UIntPtr size, uint type);
    const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000, PAGE_READWRITE = 0x04;

    readonly TextBox _log;
    readonly Button _run;
    readonly ComboBox _app;
    readonly NumericUpDown _cap;
    readonly Func<bool> _busy;
    bool _running;

    static readonly (string name, string[] procs)[] Apps =
    {
        ("Chrome", new[] { "chrome" }),
        ("Edge", new[] { "msedge" }),
        ("Яндекс Браузер", new[] { "browser" }),
        ("Telegram", new[] { "Telegram" }),
        ("Discord", new[] { "Discord" }),
    };

    public ProofTab(TextBox log, Func<bool> busy) : base("Доказательство")
    {
        _log = log; _busy = busy;
        _run = new Button { Text = "🔬 Доказать сжатие на реальной памяти", AutoSize = true, Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold) };
        _run.Click += async (_, _) => await RunProof();
        _app = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        foreach (var a in Apps) _app.Items.Add(a.name);
        _app.SelectedIndex = 0;
        _cap = new NumericUpDown { Minimum = 2000, Maximum = 120000, Value = 16000, Increment = 2000, Width = 90 };

        var stack = new VStack();
        stack.Add(Ui.L("Проверяет, что сжатие страниц реально экономит память, а не только «выглядит» так в отчёте. " +
            "Копии реальных страниц приложения упаковываются каждым кодеком в одну арену с индексом, арена кладётся в нативную память, " +
            "а её размер меряется счётчиками Windows (закоммиченная память и рабочий набор процесса). Потом каждая страница восстанавливается и сверяется по SHA-256."));
        stack.Add(Ui.Row(Ui.L("Приложение:"), _app, Ui.L("Страниц всего:"), _cap, _run));
        stack.Add(Ui.Hint("Только чтение памяти чужого процесса; копии живут в памяти ElasticMem и стираются после замера. Нулевые страницы исключены — Windows их не хранит."));
        stack.Add(_log, fill: true);
        Controls.Add(stack);
    }

    void Append(string s) { if (InvokeRequired) { BeginInvoke(() => Append(s)); return; } if (_log.TextLength > 300_000) _log.Text = _log.Text[^150_000..]; _log.AppendText(s.TrimEnd() + Environment.NewLine); }

    async Task RunProof()
    {
        if (_running || _busy()) { Append("Занято другой операцией."); return; }
        if (!OperatingSystem.IsWindows()) { Append("Только для Windows."); return; }
        _running = true; _run.Enabled = false;
        var (name, procs) = Apps[_app.SelectedIndex];
        int cap = (int)_cap.Value;
        try { Append(await Task.Run(() => Body(name, procs, cap, s => Append(s)))); }
        catch (Exception ex) { Append("Ошибка: " + ex.GetType().Name + ": " + ex.Message); }
        finally { _running = false; _run.Enabled = true; }
    }

    static (long Commit, long Ws) Mem()
    {
        Native.TryGetMemInfo((IntPtr)(-1), out var c); // псевдо-дескриптор текущего процесса
        return ((long)(ulong)c.PrivateUsage, (long)(ulong)c.WorkingSetSize);
    }

    static void Quiet() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(50); }

    static string Mib(long b) => (b / 1048576.0).ToString("0.00");

    static string Body(string label, string[] procNames, int cap, Action<string> progress)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"== Доказательство сжатия: {label}  {DateTime.Now:HH:mm:ss} ==");

        // 1. Копии реальных страниц со всех процессов приложения; нулевые исключаем.
        var procs = new List<Process>();
        foreach (var pn in procNames) procs.AddRange(Process.GetProcessesByName(pn));
        if (procs.Count == 0) { sb.AppendLine($"Процессы «{string.Join(", ", procNames)}» не найдены. Запустите приложение."); return sb.ToString(); }
        var ordered = procs.OrderByDescending(p => { try { return p.WorkingSet64; } catch { return 0L; } }).ToList();
        int perProc = Math.Max(1000, cap / ordered.Count);
        var all = new List<byte[]>();
        foreach (var p in ordered)
        {
            if (all.Count >= cap) break;
            var pg = PageSampler.Sample(p.Id, Math.Min(perProc, cap - all.Count), out _);
            if (pg != null) all.AddRange(pg);
        }
        foreach (var p in procs) p.Dispose();
        int zero = all.Count(CompressionProof.IsZero);
        var pages = all.Where(p => !CompressionProof.IsZero(p)).ToList();
        all.Clear();
        if (pages.Count < 100) { sb.AppendLine("Слишком мало ненулевых страниц прочиталось (для чужого пользователя нужны права администратора)."); return sb.ToString(); }
        int n = pages.Count;
        sb.AppendLine($"Прочитано ненулевых страниц: {n} ({Mib((long)n * 4096)} МиБ) с {ordered.Count} процессов; нулевых исключено: {zero}.");

        try
        {
            // 2. Эталон: те же страницы БЕЗ сжатия в нативной памяти — сколько это стоит по счётчикам ОС.
            progress("Эталон без сжатия…");
            Quiet();
            var r0 = Mem();
            IntPtr rawPtr = VirtualAlloc(IntPtr.Zero, (UIntPtr)((long)n * 4096), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (rawPtr == IntPtr.Zero) throw new OutOfMemoryException("VirtualAlloc raw");
            unsafe { var span = new Span<byte>((void*)rawPtr, n * 4096); for (int i = 0; i < n; i++) pages[i].CopyTo(span.Slice(i * 4096)); }
            var r1 = Mem();
            long rawCommit = r1.Commit - r0.Commit, rawWs = r1.Ws - r0.Ws;
            VirtualFree(rawPtr, UIntPtr.Zero, MEM_RELEASE);
            sb.AppendLine($"Эталон без сжатия (измерено ОС): commit +{Mib(rawCommit)} МиБ, рабочий набор +{Mib(rawWs)} МиБ.");
            sb.AppendLine();

            // Прогрев JIT, чтобы он не попал в окно замера.
            using (var warm = new PortablePageCodec("lz4")) { var wp = CompressionProof.Compress(pages.Take(2).ToList(), warm); CompressionProof.WriteArena(new byte[wp.ArenaBytes], wp); }

            var codecs = new List<(string Label, Func<IPageCodec> Make)>
            {
                ("xpress (как Windows)", () => new WindowsXpressCodec(false)),
                ("xpress-huffman", () => new WindowsXpressCodec(true)),
                ("lz4", () => new PortablePageCodec("lz4")),
                ("zstd-1", () => new PortablePageCodec("zstd1")),
                ("deflate-fast", () => new PortablePageCodec("deflate-fast")),
                ("deflate", () => new PortablePageCodec("deflate")),
                ("deflate-max", () => new PortablePageCodec("deflate-max")),
            };

            var rows = new List<(string Label, CompressionProof.Result R, long Commit, long Ws)>();
            foreach (var (lbl, make) in codecs)
            {
                progress(lbl + "…");
                using var codec = make();
                var packed = CompressionProof.Compress(pages, codec);          // сжатие — вне окна замера
                Quiet();
                var a0 = Mem();
                IntPtr ap = VirtualAlloc(IntPtr.Zero, (UIntPtr)packed.ArenaBytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (ap == IntPtr.Zero) throw new OutOfMemoryException("VirtualAlloc arena");
                long commit, ws; int ok, bad; List<double> lat; bool neg;
                try
                {
                    unsafe { CompressionProof.WriteArena(new Span<byte>((void*)ap, packed.ArenaBytes), packed); }
                    var a1 = Mem();
                    commit = a1.Commit - a0.Commit; ws = a1.Ws - a0.Ws;
                    unsafe
                    {
                        var ro = new ReadOnlySpan<byte>((void*)ap, packed.ArenaBytes);
                        (ok, bad, lat) = CompressionProof.Verify(ro, packed, codec);   // восстановление из НАТИВНОЙ арены
                        neg = CompressionProof.NegativeControl(ro, packed, codec);
                    }
                }
                finally { VirtualFree(ap, UIntPtr.Zero, MEM_RELEASE); }
                rows.Add((lbl, CompressionProof.Summarize(lbl, packed, ok, bad, lat, neg), commit, ws));
            }

            sb.AppendLine("Кодек                | арена (учёт) | commit по ОС | экономия по ОС | целостность    | порча поймана | распаковка p50/p95 мкс | сжатие МБ/с");
            foreach (var (lbl, r, commit, ws) in rows)
            {
                double saving = rawCommit > 0 ? (1.0 - (double)commit / rawCommit) * 100 : 0;
                sb.AppendLine($"{lbl,-20} | {r.RatioPercent,10:0.0}% | {Mib(commit),9} МиБ | {saving,12:0.0}% | {r.IntegrityOk,6}/{r.Pages,-6} | {(r.NegativeControlDetected ? "да" : "НЕТ"),-13} | {r.DecodeP50Us,8:0.0} / {r.DecodeP95Us,-8:0.0}     | {r.EncodeMBps,8:0}");
            }

            // 3. Выводы — только по доказанным строкам, на одних и тех же страницах.
            sb.AppendLine();
            var proven = rows.Where(x => x.R.Proven).ToList();
            var notProven = rows.Where(x => !x.R.Proven).Select(x => x.Label).ToList();
            sb.AppendLine(notProven.Count == 0
                ? $"Целостность: ВСЕ кодеки восстановили все {n} страниц побайтово (SHA-256), отрицательный контроль сработал у каждого."
                : "ВНИМАНИЕ, не доказано (не использовать выводы по ним): " + string.Join(", ", notProven));
            long maxDrift = rows.Max(x => Math.Abs(x.Commit - (((long)x.R.ArenaBytes + 4095) / 4096) * 4096));
            sb.AppendLine($"Учёт против ОС: расхождение размера арены и закоммиченной памяти по счётчику ОС не больше {Mib(maxDrift)} МиБ — цифры не «нарисованы».");
            var xp = proven.FirstOrDefault(x => x.Label.StartsWith("xpress (как"));
            var best = proven.OrderBy(x => x.Commit).FirstOrDefault();
            if (xp.Label != null && best.Label != null)
            {
                double vsXp = xp.Commit > 0 ? (1.0 - (double)best.Commit / xp.Commit) * 100 : 0;
                sb.AppendLine($"Лучший по реальной памяти: {best.Label} — {Mib(best.Commit)} МиБ против {Mib(xp.Commit)} МиБ у XPRESS (то, чем сжимает Windows).");
                sb.AppendLine(vsXp >= 1
                    ? $"Итог: на этих {n} реальных страницах {best.Label} занимает на {vsXp:0.0}% меньше физической памяти, чем XPRESS, без потерь. Цена — распаковка {best.R.DecodeP50Us:0.0} мкс против {xp.R.DecodeP50Us:0.0} мкс на страницу."
                    : "Итог: на этих страницах ни один кодек заметно не обогнал XPRESS — своё хранилище не даст экономии сверх Windows.");
            }
            sb.AppendLine("Важно: Windows Memory Compression всегда использует XPRESS, заменить кодек ОС нельзя. Этот выигрыш достижим только для памяти, которой управляет наше хранилище (своя сжатая куча / ElasticStore).");
        }
        finally { foreach (var p in pages) Array.Clear(p); pages.Clear(); }
        return sb.ToString();
    }
}
