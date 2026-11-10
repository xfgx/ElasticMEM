namespace ElasticMem.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        void Crash(Exception ex){try{var folder=Path.Combine(AppSettings.Dir,"diagnostics");Directory.CreateDirectory(folder);File.AppendAllText(Path.Combine(folder,"crash.log"),DateTime.UtcNow.ToString("O")+"\n"+ex+"\n");}catch{}}
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if(e.ExceptionObject is Exception ex)Crash(ex); };
        Application.ThreadException += (_, e) => { Crash(e.Exception); MessageBox.Show("Ошибка записана в локальный crash.log.","ElasticMem"); };
        TaskScheduler.UnobservedTaskException += (_,e) => { Crash(e.Exception); e.SetObserved(); };
        if (Updater.TryWriteVersion(args)) return;
        ApplicationConfiguration.Initialize();
        if (Updater.TryApply(args)) return;
        if (Cli.TryRun(args)) return;
        var opts = new StartupOptions { RawArgs = args };
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--agent": opts.StartAgent = true; break;
                case "--all": opts.All = true; break;
                case "--process": if (i + 1 < args.Length) { opts.Process = args[++i]; opts.All = false; } break;
                case "--min-idle": if (i + 1 < args.Length && double.TryParse(args[++i], out var m)) opts.MinIdle = m; break;
                case "--tray": opts.Tray = true; break;
                case "--updated": opts.UpdatedFrom = i + 1 < args.Length ? args[++i] : "?"; break;
            }
        }
        Application.Run(new MainForm(opts));
    }
}

public sealed class StartupOptions
{
    public bool StartAgent, Tray, All = true;
    public string Process = "";
    public string? UpdatedFrom;
    public string[] RawArgs = Array.Empty<string>();
    public double MinIdle = 30;
}
