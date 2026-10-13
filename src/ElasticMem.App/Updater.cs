using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ElasticMem.App;

public sealed class AppSettings
{
    public string UpdateSource { get; set; } = ReportUploader.DefaultUpdateSource;
    public bool SendReports { get; set; } = false;
    public string Theme { get; set; } = "system"; // system | dark | light
    public bool CheckOnStart { get; set; } = false;
    public bool AutoInstall { get; set; } = false;
    public DateTime LastCheck { get; set; }

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ElasticMem");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
                s.SendReports = false;
                if (string.IsNullOrWhiteSpace(s.UpdateSource)) s.UpdateSource = ReportUploader.DefaultUpdateSource;
                return s;
            }
        }
        catch { }
        return new();
    }
    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed record UpdateInfo(Version Version, string DownloadUrl, string? Sha256, string Notes, string Source);

/// <summary>
/// Автообновление. Источник:
///   • «owner/repo» или https://github.com/owner/repo — последний релиз GitHub (asset *.zip или ElasticMem*.exe);
///   • ссылка на манифест *.json: {"version":"0.4.1","url":"https://…/ElasticMem.zip","sha256":"…","notes":"…"};
///   • прямая ссылка на *.zip / *.exe (версия определяется после скачивания);
///   • локальный файл (кнопка «Установить из файла»).
/// Замена: новый exe запускается из временной папки с --apply-update, ждёт выхода старого процесса,
/// сохраняет копию ElasticMem.exe.bak, копирует себя на место старого и перезапускает программу с теми же аргументами.
/// </summary>
public static class Updater
{
    public static Version Current =>
        Assembly.GetEntryAssembly()?.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ElasticMem.exe");
    public static string BackupPath => ExePath + ".bak";
    private static string WorkDir => Path.Combine(Path.GetTempPath(), "ElasticMem_update");

    private static readonly HttpClient Http = CreateHttp();
    private static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("ElasticMem-Updater/" + Current);
        return h;
    }

    public static Version? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(new[] { '-', '+', ' ' }); if (cut > 0) s = s[..cut];
        if (!Version.TryParse(s.Contains('.') ? s : s + ".0", out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    private static (string owner, string repo)? GitHubRepo(string src)
    {
        src = src.Trim().TrimEnd('/');
        if (src.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) src = src[19..];
        else if (src.Contains("://")) return null;
        var parts = src.Split('/');
        if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0) return null;
        return (parts[0], parts[1].EndsWith(".git") ? parts[1][..^4] : parts[1]);
    }

    /// <summary>Узнать, какая версия доступна. Для прямой ссылки на файл версия = 0.0.0 (выяснится после скачивания).</summary>
    public static async Task<UpdateInfo> CheckAsync(string source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new InvalidOperationException("Не задан источник обновлений.");
        source = source.Trim();
        if (GitHubRepo(source) is { } gh && !source.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var json = JsonNode.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{gh.owner}/{gh.repo}/releases/latest", ct))!;
            var ver = ParseVersion((string?)json["tag_name"]) ?? ParseVersion((string?)json["name"]) ?? throw new InvalidOperationException("В релизе нет номера версии (tag_name вида v0.4.1).");
            JsonNode? best = null; int bestScore = -1;
            foreach (var a in json["assets"]?.AsArray() ?? new JsonArray())
            {
                string n = ((string?)a?["name"] ?? "").ToLowerInvariant();
                int score = n.EndsWith(".zip") && n.Contains("win") ? 3 : n.EndsWith(".exe") && n.Contains("elasticmem") ? 2 : n.EndsWith(".zip") && !n.Contains("source") ? 1 : -1;
                if (score > bestScore) { bestScore = score; best = a; }
            }
            if (best == null || bestScore < 0) throw new InvalidOperationException("В последнем релизе нет файла ElasticMem*.exe или *win*.zip.");
            string? sha = ((string?)best["digest"]) is string d && d.StartsWith("sha256:") ? d[7..] : null;
            return new UpdateInfo(ver, (string)best["browser_download_url"]!, sha, (string?)json["body"] ?? "", "GitHub " + gh.owner + "/" + gh.repo);
        }
        if (source.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var json = JsonNode.Parse(await Http.GetStringAsync(source, ct))!;
            var ver = ParseVersion((string?)json["version"]) ?? throw new InvalidOperationException("В манифесте нет поля version.");
            string url = (string?)json["url"] ?? throw new InvalidOperationException("В манифесте нет поля url.");
            if (!url.Contains("://")) url = new Uri(new Uri(source), url).ToString();
            return new UpdateInfo(ver, url, (string?)json["sha256"], (string?)json["notes"] ?? "", "манифест");
        }
        if (source.Contains("://")) return new UpdateInfo(new Version(0, 0, 0), source, null, "", "прямая ссылка");
        throw new InvalidOperationException("Источник не распознан. Укажите owner/repo GitHub, ссылку на manifest.json или на .zip/.exe.");
    }

    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<string> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(WorkDir);
        string name = Path.GetFileName(new Uri(info.DownloadUrl).LocalPath);
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) name = "update.bin";
        string file = Path.Combine(WorkDir, DateTime.Now.ToString("yyyyMMdd_HHmmss_") + name);
        using var resp = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(file))
        {
            var buf = new byte[1 << 16]; long got = 0; int n; var sw = Stopwatch.StartNew();
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct); got += n;
                if (sw.ElapsedMilliseconds > 300) { sw.Restart(); progress.Report(total > 0 ? $"Скачано {got >> 20} из {total >> 20} МБ ({100 * got / total}%)" : $"Скачано {got >> 20} МБ"); }
            }
        }
        progress.Report("Скачано: " + (new FileInfo(file).Length >> 20) + " МБ");
        if (!string.IsNullOrEmpty(info.Sha256))
        {
            string h = Sha256(file);
            if (!h.Equals(info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Контрольная сумма не совпала: ожидалось {info.Sha256}, получено {h}. Файл повреждён или подменён.");
            progress.Report("SHA-256 совпадает.");
        }
        return file;
    }

    public static string Sha256(string file) { using var s = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }

    /// <summary>Из .zip или .exe получить проверенный ElasticMem.exe во временной папке. Возвращает путь и его версию.</summary>
    public static (string exe, Version ver) Prepare(string file)
    {
        Directory.CreateDirectory(WorkDir);
        string exe;
        if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            string dir = Path.Combine(WorkDir, "x_" + DateTime.Now.ToString("HHmmssfff"));
            ZipFile.ExtractToDirectory(file, dir, overwriteFiles: true);
            exe = Directory.EnumerateFiles(dir, "ElasticMem.exe", SearchOption.AllDirectories).FirstOrDefault()
                  ?? Directory.EnumerateFiles(dir, "ElasticMem*.exe", SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new InvalidOperationException("В архиве нет ElasticMem.exe.");
        }
        else exe = file;

        using (var fs = File.OpenRead(exe))
            if (fs.Length < 1 << 20 || fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') throw new InvalidOperationException("Файл не похож на программу Windows (exe).");
        string staged0 = Path.Combine(WorkDir, $"probe_{DateTime.Now:HHmmssfff}.exe");
        File.Copy(exe, staged0, true);
        var ver = Probe(staged0);
        string staged = Path.Combine(WorkDir, $"ElasticMem_{ver}_{DateTime.Now:HHmmssfff}.exe");
        File.Move(staged0, staged, true);
        return (staged, ver);
    }

    /// <summary>
    /// Спросить у самого exe, что он такое: запуск с --version-file. Свойства файла не годятся —
    /// при сборке single-file не на Windows в exe нет ресурса версии.
    /// </summary>
    private static Version Probe(string exe)
    {
        string outFile = Path.Combine(WorkDir, $"ver_{Guid.NewGuid():N}.txt");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--version-file"); psi.ArgumentList.Add(outFile);
        using (var p = Process.Start(psi)!)
            if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } throw new InvalidOperationException("Новая версия не ответила за 60 с."); }
        if (!File.Exists(outFile)) throw new InvalidOperationException("Файл не является ElasticMem с поддержкой обновлений (v0.4+). Версии до 0.4 ставьте вручную.");
        var parts = File.ReadAllText(outFile).Trim().Split('|');
        try { File.Delete(outFile); } catch { }
        if (parts.Length < 2 || parts[0] != "ElasticMem") throw new InvalidOperationException("Это не ElasticMem.");
        return ParseVersion(parts[1]) ?? throw new InvalidOperationException("Не удалось определить версию.");
    }

    /// <summary>Ответ на --version-file: «ElasticMem|0.4.0».</summary>
    public static bool TryWriteVersion(string[] args)
    {
        int i = Array.IndexOf(args, "--version-file");
        if (i < 0 || i + 1 >= args.Length) return false;
        try { File.WriteAllText(args[i + 1], "ElasticMem|" + Current); } catch { }
        return true;
    }

    private static bool CanWrite(string dir)
    {
        try { string t = Path.Combine(dir, ".em_write_test"); File.WriteAllText(t, "x"); File.Delete(t); return true; } catch { return false; }
    }

    /// <summary>Запустить установку подготовленного exe. Текущий процесс после этого должен завершиться.</summary>
    public static void Launch(string stagedExe, string[] restartArgs)
    {
        string target = ExePath;
        var psi = new ProcessStartInfo(stagedExe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(stagedExe)! };
        psi.ArgumentList.Add("--apply-update"); psi.ArgumentList.Add(target); psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add("--from"); psi.ArgumentList.Add(Current.ToString());
        psi.ArgumentList.Add("--");
        foreach (var a in restartArgs.Where(a => a != "--updated")) psi.ArgumentList.Add(a);
        if (!CanWrite(Path.GetDirectoryName(target)!)) psi.Verb = "runas"; // Program Files → нужны права администратора
        Process.Start(psi);
    }

    /// <summary>Режим установщика: ElasticMem_new.exe --apply-update &lt;target&gt; &lt;pid&gt; --from 0.3.0 -- &lt;args&gt;</summary>
    public static bool TryApply(string[] args)
    {
        int i = Array.IndexOf(args, "--apply-update");
        if (i < 0 || i + 2 >= args.Length) return false;
        string target = args[i + 1];
        int.TryParse(args[i + 2], out int pid);
        int fi = Array.IndexOf(args, "--from"); string from = fi >= 0 && fi + 1 < args.Length ? args[fi + 1] : "?";
        int sep = Array.IndexOf(args, "--");
        var rest = sep >= 0 ? args[(sep + 1)..] : Array.Empty<string>();
        string self = Environment.ProcessPath!;
        string log = Path.Combine(AppSettings.Dir, "update.log");
        void Log(string s) { try { Directory.CreateDirectory(AppSettings.Dir); File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {s}{Environment.NewLine}"); } catch { } }
        try
        {
            Log($"Обновление {from} → {Current}: {self} → {target}");
            if (pid > 0)
            {
                try { using var p = Process.GetProcessById(pid); if (!p.WaitForExit(30000)) { Log("Старый процесс не завершился за 30 с — завершаю."); p.Kill(); p.WaitForExit(5000); } }
                catch (ArgumentException) { }
            }
            // другие копии ElasticMem из той же папки (например, агент в трее) тоже держат файл
            foreach (var p in Process.GetProcessesByName("ElasticMem"))
            {
                try { if (p.Id != Environment.ProcessId && string.Equals(p.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase)) { p.CloseMainWindow(); if (!p.WaitForExit(5000)) p.Kill(); } }
                catch { }
                finally { p.Dispose(); }
            }
            if (File.Exists(target)) File.Copy(target, target + ".bak", true);
            Exception? last = null;
            for (int k = 0; k < 40; k++)
            {
                try { File.Copy(self, target, true); last = null; break; }
                catch (Exception ex) { last = ex; Thread.Sleep(500); }
            }
            if (last != null) throw last;
            Log("Файл заменён.");
            var psi = new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(target)! };
            foreach (var a in rest) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--updated"); psi.ArgumentList.Add(from);
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Log("Ошибка: " + ex);
            try { if (File.Exists(target + ".bak") && !File.Exists(target)) File.Copy(target + ".bak", target, true); } catch { }
            MessageBox.Show("Не удалось установить обновление:\n" + ex.Message + "\n\nЖурнал: " + log, "ElasticMem — обновление", MessageBoxButtons.OK, MessageBoxIcon.Error);
            try { if (File.Exists(target)) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
            return true;
        }
    }

    /// <summary>Убрать старые временные файлы обновлений (кроме текущего процесса).</summary>
    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(WorkDir)) return;
            foreach (var f in Directory.EnumerateFiles(WorkDir))
                try { if (!string.Equals(f, ExePath, StringComparison.OrdinalIgnoreCase) && File.GetLastWriteTime(f) < DateTime.Now.AddMinutes(-10)) File.Delete(f); } catch { }
            foreach (var d in Directory.EnumerateDirectories(WorkDir))
                try { if (Directory.GetLastWriteTime(d) < DateTime.Now.AddMinutes(-10)) Directory.Delete(d, true); } catch { }
        }
        catch { }
    }
}
