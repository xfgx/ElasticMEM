using ElasticMem.Core;

namespace ElasticMem.App;

/// <summary>
/// Режим без окна для тестирования:
///   ElasticMem.exe --selftest --out C:\temp\self.txt
///   ElasticMem.exe --scan telegram,chrome,steam,yandex,dota2 [--trim] [--limit 512] [--out C:\temp\report.txt] [--send]
/// </summary>
internal static class Cli
{
    public static bool TryRun(string[] args)
    {
        bool self = args.Contains("--selftest");
        int si = Array.FindIndex(args, a => a == "--scan");
        if (!self && si < 0) return false;
        string outPath = Arg(args, "--out") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElasticMem", "reports", $"cli_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var sb = new System.Text.StringBuilder(AppsTab.SystemInfo() + Environment.NewLine + Environment.NewLine);
        if (self) sb.AppendLine(SelfTest.Run());
        if (si >= 0 && si + 1 < args.Length)
        {
            var keys = args[si + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var o = new ProfileScanner.Options { RunTrimTest = args.Contains("--trim") };
            if (long.TryParse(Arg(args, "--limit"), out long lim)) { o.TotalLimitBytes = lim << 20; o.PerProcessLimitBytes = Math.Max(64L << 20, (lim << 20) / 2); }
            foreach (var k in keys)
            {
                var prof = AppProfile.Builtin.FirstOrDefault(p => p.Title.Contains(k, StringComparison.OrdinalIgnoreCase) || p.ProcessNames.Any(n => n.Contains(k, StringComparison.OrdinalIgnoreCase)))
                           ?? new AppProfile("Свой: " + k, new[] { k }, Array.Empty<string>(), "");
                try { sb.AppendLine(ProfileScanner.Run(prof, o, new Progress<string>(_ => { }), CancellationToken.None)); }
                catch (Exception ex) { sb.AppendLine($"{prof.Title}: ошибка — {ex.Message}"); }
                sb.AppendLine();
            }
        }
        File.WriteAllText(outPath, sb.ToString());
        string sent = "";
        if(args.Contains("--send")) sent="\nСетевая отправка удалена. Пришлите отчёт вручную.";
        if(!args.Contains("--no-dialog")) MessageBox.Show("Отчёт сохранён:\n" + outPath + sent, "ElasticMem");
        return true;
    }

    private static string? Arg(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
}
