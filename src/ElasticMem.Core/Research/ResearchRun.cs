using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ElasticMem.Core.Research;

public sealed class ResearchRun : IDisposable
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public string DirectoryPath { get; }
    public string Mode { get; }
    public string Status { get; private set; } = "running";
    public Dictionary<string, object?> Metadata { get; } = new();
    private bool _sealed;
    public bool IsSealed => _sealed;
    public void Invalidate(Exception error)
    {
        if(File.Exists(Path.Combine(DirectoryPath,"manifest.json")))File.Move(Path.Combine(DirectoryPath,"manifest.json"),Path.Combine(DirectoryPath,"invalidated-manifest.json"),true);
        _sealed=false;Metadata.Remove("artifacts");Seal("invalid_integrity_not_result","Integrity verification failed. Prior manifest retained for diagnosis.",error);
    }
    public ResearchRun(string root, string mode, string version, string commit, object parameters)
    {
        if (mode.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("Invalid mode");
        Mode = mode;
        DirectoryPath = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "_" + mode + "_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(DirectoryPath);
        Metadata["schema"] = "elasticmem.app.run.v1"; Metadata["mode"] = mode;
        Metadata["version"] = version; Metadata["code_commit"] = commit; Metadata["parameters"] = parameters;
        Metadata["utc_started"] = DateTime.UtcNow; Metadata["os"] = Environment.OSVersion.ToString();
        Metadata["architecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        Metadata["runtime"] = Environment.Version.ToString();
    }
    public string FilePath(string name)
    {
        if (_sealed) throw new InvalidOperationException("Run is sealed");
        var path = Path.GetFullPath(Path.Combine(DirectoryPath, name));
        if (!path.StartsWith(Path.GetFullPath(DirectoryPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Unsafe path");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); return path;
    }
    public void WriteJson(string name, object value) => File.WriteAllText(FilePath(name), JsonSerializer.Serialize(value, Json));
    public void Log(string text) => File.AppendAllText(FilePath("events.log"), DateTime.UtcNow.ToString("O") + " " + text + Environment.NewLine);
    public static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    public string CopyInput(string source, string name)
    {
        string hash = Hash(source), target = FilePath("inputs/" + name); File.Copy(source, target, false);
        if (Hash(target) != hash || Hash(source) != hash) throw new IOException("Input changed during copy"); return target;
    }
    public void Seal(string status, string summary, Exception? error = null)
    {
        if (_sealed) return; Status = status;
        if (error != null) File.WriteAllText(FilePath("error.txt"), error.ToString());
        Metadata["status"] = status; Metadata["utc_finished"] = DateTime.UtcNow;
        File.WriteAllText(FilePath("report.md"), $"# ElasticMem: {Mode}\n\nStatus: {status}\nVersion: {Metadata["version"]}\nCommit: {Metadata["code_commit"]}\n\n{summary}\n\nHashes verify integrity, not experimental validity.\n");
        Metadata["artifacts"] = Directory.EnumerateFiles(DirectoryPath, "*", SearchOption.AllDirectories).Where(p => p != Path.Combine(DirectoryPath,"manifest.json")).Select(p => new { path = Path.GetRelativePath(DirectoryPath, p).Replace('\\','/'), bytes = new FileInfo(p).Length, sha256 = Hash(p) }).ToArray();
        WriteJson("manifest.json", Metadata); _sealed = true;
    }
    public void Dispose() { if (!_sealed) Seal("interrupted", "Run did not finish. Partial artifacts are not a successful result."); }
    public static void Verify(string folder)
    {
        folder = Path.GetFullPath(folder);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in doc.RootElement.GetProperty("artifacts").EnumerateArray())
        {
            string rel = a.GetProperty("path").GetString()!, path = Path.GetFullPath(Path.Combine(folder, rel));
            if (!path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !listed.Add(path)) throw new InvalidDataException("Unsafe/duplicate artifact");
            for(string? ancestor=path;ancestor!=null&&ancestor!=folder;ancestor=Path.GetDirectoryName(ancestor))if((File.GetAttributes(ancestor)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Reparse-point artifact rejected");
            if (!File.Exists(path) || new FileInfo(path).Length != a.GetProperty("bytes").GetInt64() || Hash(path) != a.GetProperty("sha256").GetString()) throw new InvalidDataException("Changed artifact: " + rel);
        }
        var actual = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(p => p != Path.Combine(folder, "manifest.json")).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!listed.SetEquals(actual)) throw new InvalidDataException("Unexpected/missing files in run");
    }
    public static string ExportLogs(string root, string target, bool includeSensitive)
    {
        if (File.Exists(target)) throw new IOException("Export target already exists");
        var files = Directory.Exists(root) ? Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToArray() : Array.Empty<string>();
        var index = new List<object>();var sourceVerification=new List<object>();
        if(Directory.Exists(root))foreach(var folder in Directory.EnumerateDirectories(root))
        {
            if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)continue;
            if(File.Exists(Path.Combine(folder,"manifest.json"))){Verify(folder);sourceVerification.Add(new{run=Path.GetFileName(folder),source_integrity="verified_before_export"});}
            else if(Path.GetFileName(folder)!="diagnostics")throw new InvalidDataException("Incomplete run: "+Path.GetFileName(folder));
        }
        var allowedNames=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"manifest.json","report.md","result.json","events.log","error.txt","process-samples.csv","gpu-samples.csv","gpu-device.json","dma-samples.csv","disk-samples.csv","codec-samples.csv","kernel-samples.csv","application-compression-summary.csv","epoch-counters.csv","epoch-health.csv","mma-query.json","layout-audit.json","crash.log","invalidated-manifest.json","auto-report.txt","applications-report.txt","live-ab-report.txt","live-ab-progress.json","live-ipc-error.json"};
        using (var zip = ZipFile.Open(target, ZipArchiveMode.Create))
        {
            foreach (var f in files)
            {
                string rel = Path.GetRelativePath(root, f).Replace('\\','/');
                if (Path.GetFullPath(f) == Path.GetFullPath(target)) continue;
                // Dumps and full traces may contain private data. Excluded unless explicitly requested.
                bool sensitive = rel.Contains("/inputs/", StringComparison.OrdinalIgnoreCase) || rel.Contains("/snapshots/", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || (rel.EndsWith(".emd", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".erb", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".ecb", StringComparison.OrdinalIgnoreCase)) || rel.Contains("screenshots/", StringComparison.OrdinalIgnoreCase);
                if (sensitive && !includeSensitive) continue;
                string extension=Path.GetExtension(rel).ToLowerInvariant();
                if(!includeSensitive && (rel.Split('/').Length!=2 || !(allowedNames.Contains(Path.GetFileName(f)) || System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"^(?:residency-summary|epoch-health)-[0-9]+\.json$"))))continue;
                if ((File.GetAttributes(f) & FileAttributes.ReparsePoint) != 0) continue;
                var entry = zip.CreateEntry(rel, CompressionLevel.Fastest);
                if(!includeSensitive && new FileInfo(f).Length<=16L*1024*1024)
                {
                    string text=File.ReadAllText(f);string profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if(!string.IsNullOrEmpty(profile))text=text.Replace(profile,"%USERPROFILE%",StringComparison.OrdinalIgnoreCase);
                    if(Path.GetFileName(f)=="manifest.json")
                    {
                        try{var node=System.Text.Json.Nodes.JsonNode.Parse(text);if(node?["parameters"] is System.Text.Json.Nodes.JsonObject parameters)foreach(var key in new[]{"trace","metadata","scratch","source_run"})if(parameters.ContainsKey(key))parameters[key]="<redacted path>";text=node?.ToJsonString(Json)??text;}catch{}
                    }
                    byte[] data=System.Text.Encoding.UTF8.GetBytes(text);using(var destination=entry.Open())destination.Write(data);
                    index.Add(new { path=rel,bytes=(long)data.Length,sha256=Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() });continue;
                }
                if(!includeSensitive && new FileInfo(f).Length>16L*1024*1024)throw new InvalidDataException("Log too large for redacted export: "+rel);
                using var output = entry.Open(); using var input = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; int n; long bytes=0;
                while((n=input.Read(buffer))>0){output.Write(buffer,0,n);hash.AppendData(buffer,0,n);bytes+=n;}
                index.Add(new { path = rel, bytes, sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() });
            }
            var manifest = zip.CreateEntry("EXPORT.json"); using var writer = new StreamWriter(manifest.Open()); writer.Write(JsonSerializer.Serialize(new { schema = "elasticmem.logs.v1", utc = DateTime.UtcNow, sensitive_data_included = includeSensitive, note = "Redacted export may omit source inputs; it is not a self-contained reproduction package. Review before sending.", source_run_verification=sourceVerification,files = index }, Json));
        }
        File.WriteAllText(target + ".sha256", Hash(target) + "  " + Path.GetFileName(target)); return target;
    }
}
