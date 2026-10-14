using ElasticMem.App.Research;
namespace ElasticMem.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        void Crash(Exception ex){try{var folder=Path.Combine(ResearchJobs.Root,"diagnostics");Directory.CreateDirectory(folder);File.AppendAllText(Path.Combine(folder,"crash.log"),DateTime.UtcNow.ToString("O")+"\n"+ex+"\n");}catch{}}
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if(e.ExceptionObject is Exception ex)Crash(ex); };
        Application.ThreadException += (_, e) => { Crash(e.Exception); MessageBox.Show("Ошибка записана в локальный crash.log. Экспортируйте логи из лаборатории.","ElasticMem"); };
        TaskScheduler.UnobservedTaskException += (_,e) => { Crash(e.Exception); e.SetObserved(); };
        if (Updater.TryWriteVersion(args)) return;
        if (ResearchCli.TryRun(args)) return;
        ApplicationConfiguration.Initialize();
        if (Updater.TryApply(args)) return;
        if (Cli.TryRun(args)) return;
        var opts = new StartupOptions { RawArgs = args };
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--backend-agent": opts.BackendPanel=true;opts.Tray=true;break;
                case "--backend-ui": opts.BackendPanel=true;break;
                case "--agent": opts.StartAgent = true; break;
                case "--all": opts.All = true; break;
                case "--process": if (i + 1 < args.Length) { opts.Process = args[++i]; opts.All = false; } break;
                case "--min-idle": if (i + 1 < args.Length && double.TryParse(args[++i], out var m)) opts.MinIdle = m; break;
                case "--tray": opts.Tray = true; break;
                case "--updated": opts.UpdatedFrom = i + 1 < args.Length ? args[++i] : "?"; break;
            }
        }
        if(opts.BackendPanel)opts.StartAgent=false; // Backend mode must not start the legacy host trim agent.
        using var singleton=args.Contains("--backend-agent")?new System.Threading.Mutex(false,"Local\\ElasticMem.BackendAgent."+System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value):null;
        bool held=false;try{if(singleton!=null){held=singleton.WaitOne(0);if(!held)return;}Application.Run(new MainForm(opts));}finally{if(held)singleton!.ReleaseMutex();}
    }
}

public sealed class StartupOptions
{
    public bool StartAgent, Tray, All = true, BackendPanel;
    public string Process = "";
    public string? UpdatedFrom;
    public string[] RawArgs = Array.Empty<string>();
    public double MinIdle = 30;
}
