using System.Diagnostics;
using ElasticMem.Core.Research;

namespace ElasticMem.App;

/// <summary>
/// Вкладка «Дедуп»: меряет РЕАЛЬНЫЙ потолок дедупликации между процессами одного приложения.
/// Собирает копии страниц со ВСЕХ процессов выбранного приложения (например, всех renderer'ов браузера),
/// кладёт их чанки в настоящий content-addressed DedupStore (SHA-256 + Deflate + refcount) и сравнивает
/// «дедуп → сжатие» с обычным per-page сжатием. Только чтение; копии стираются сразу после замера.
/// </summary>
public sealed class DedupTab : TabPage
{
    readonly TextBox _log;
    readonly Button _run, _selftest;
    readonly ComboBox _app;
    readonly NumericUpDown _cap;
    readonly Func<bool> _busy;
    bool _running;

    static readonly (string name, string[] procs)[] Apps =
    {
        ("Edge", new[] { "msedge" }),
        ("Chrome", new[] { "chrome" }),
        ("Яндекс Браузер", new[] { "browser" }),
        ("Telegram", new[] { "Telegram" }),
        ("Discord", new[] { "Discord" }),
        ("Все браузеры (Edge+Chrome+Яндекс)", new[] { "msedge", "chrome", "browser" }),
    };

    public DedupTab(TextBox log, Func<bool> busy) : base("Дедуп")
    {
        _log = log; _busy = busy;
        _run = new Button { Text = "🧮 Измерить дедуп (все процессы приложения)", AutoSize = true, Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold) };
        _run.Click += async (_, _) => await RunMeasure();
        _selftest = new Button { Text = "Самопроверка", AutoSize = true };
        _selftest.Click += async (_, _) => await RunSelfTest();
        _app = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
        foreach (var a in Apps) _app.Items.Add(a.name);
        _app.SelectedIndex = 0;
        _cap = new NumericUpDown { Minimum = 2000, Maximum = 120000, Value = 24000, Increment = 2000, Width = 90 };

        var stack = new VStack();
        stack.Add(Ui.L("Проверяет идею «дедуп как новый принцип памяти»: сколько памяти повторяется МЕЖДУ процессами одного приложения. " +
            "Windows Memory Compression и прежние механизмы сжимают каждую страницу по отдельности и этого не видят. " +
            "Здесь страницы со всех процессов приложения режутся на чанки и складываются в настоящий дедуп-store (SHA-256), затем сравниваются с обычным per-page сжатием."));
        stack.Add(Ui.Row(Ui.L("Приложение:"), _app, Ui.L("Лимит страниц всего:"), _cap, _run));
        stack.Add(Ui.Row(_selftest));
        stack.Add(Ui.Hint("Только чтение памяти; копии страниц стираются сразу после замера и никуда не отправляются. Для процессов другого пользователя нужны права администратора."));
        stack.Add(_log, fill: true);
        Controls.Add(stack);
    }

    void Append(string s) { if (InvokeRequired) { BeginInvoke(() => Append(s)); return; } if (_log.TextLength > 200_000) _log.Text = _log.Text[^100_000..]; _log.AppendText(s.TrimEnd() + Environment.NewLine); }

    async Task RunSelfTest()
    {
        if (_running || _busy()) { Append("Занято другой операцией."); return; }
        _running = true; _selftest.Enabled = false;
        try { Append(await Task.Run(DedupAnalyzer.SelfTest)); }
        catch (Exception ex) { Append("Ошибка: " + ex.Message); }
        finally { _running = false; _selftest.Enabled = true; }
    }

    async Task RunMeasure()
    {
        if (_running || _busy()) { Append("Занято другой операцией."); return; }
        if (!OperatingSystem.IsWindows()) { Append("Только для Windows."); return; }
        _running = true; _run.Enabled = false;
        var (name, procs) = Apps[_app.SelectedIndex];
        int cap = (int)_cap.Value;
        try { Append(await Task.Run(() => Measure(name, procs, cap))); }
        catch (Exception ex) { Append("Ошибка: " + ex.GetType().Name + ": " + ex.Message); }
        finally { _running = false; _run.Enabled = true; }
    }

    static string Measure(string label, string[] procNames, int totalCap)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"== Дедуп {label}  {DateTime.Now:HH:mm:ss} ==");
        var pids = new List<Process>();
        foreach (var n in procNames) pids.AddRange(Process.GetProcessesByName(n));
        if (pids.Count == 0) { sb.AppendLine($"Процессы «{string.Join(", ", procNames)}» не найдены. Запустите приложение и откройте пару вкладок/окон."); return sb.ToString(); }

        // Крупные процессы первыми; делим лимит поровну, но не меньше 1000 страниц на процесс.
        var ordered = pids.OrderByDescending(p => { try { return p.WorkingSet64; } catch { return 0L; } }).ToList();
        int perProc = Math.Max(1000, totalCap / ordered.Count);
        var all = new List<byte[]>();
        int used = 0;
        foreach (var p in ordered)
        {
            if (all.Count >= totalCap) break;
            int quota = Math.Min(perProc, totalCap - all.Count);
            List<byte[]>? pages;
            try { pages = PageSampler.Sample(p.Id, quota, out string? err); if (pages == null) { sb.AppendLine($"  PID {p.Id} ({p.ProcessName}): пропущен — {err}"); continue; } }
            catch (Exception ex) { sb.AppendLine($"  PID {p.Id}: пропущен — {ex.Message}"); continue; }
            all.AddRange(pages);
            used++;
            sb.AppendLine($"  PID {p.Id} ({p.ProcessName}): {pages.Count} страниц");
        }
        foreach (var p in pids) p.Dispose();
        if (all.Count == 0) { sb.AppendLine("Не удалось прочитать ни одной страницы (для чужого пользователя нужны права администратора)."); return sb.ToString(); }

        sb.AppendLine($"Собрано {all.Count} страниц ({all.Count * 4096 / 1048576.0:0.0} МиБ) с {used} процессов.");
        sb.AppendLine();
        try
        {
            var rep = DedupAnalyzer.Measure(all);
            sb.Append(DedupAnalyzer.Format(rep));
        }
        finally { foreach (var pg in all) Array.Clear(pg); all.Clear(); }
        return sb.ToString();
    }
}
