using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ElasticMem.Core.Backend;
using ElasticMem.Core.Research;
namespace ElasticMem.App.Backend;
internal sealed class BackendController
{
    readonly SemaphoreSlim gate=new(1,1);string selectedProfileId="";
    public void PinProfile(string id){if(Catalog().ActiveId!=id)throw new InvalidOperationException("Profile changed in another process; select again");selectedProfileId=id;}
    public string PrivateFolder {get;}=Path.Combine(AppSettings.Dir,"backend-private");
    public string ConfigPath=>Path.Combine(ProtectedFolder,WindowsIdentity.GetCurrent().User!.Value+".json");
    string CredentialFor(BackendProfile p)=>Path.Combine(PrivateFolder,VmCredentialScope.FileName(p.VmId));
    public string LogPath=>Path.Combine(PrivateFolder,"events.jsonl");
    static string ProtectedFolder=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"ElasticMemBackendTools");
    public static bool Admin=>new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    static void NotReparse(string path)
    {
        for(string? p=Path.GetFullPath(path);p!=null;p=Path.GetDirectoryName(p))
            if(Directory.Exists(p)||File.Exists(p))if((File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Reparse path rejected");
    }
    static void ProtectDirectory(string path,bool privateUser)
    {
        NotReparse(path);bool existing=Directory.Exists(path);
        if(existing&&!privateUser)
        {
            var owner=new DirectoryInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value;
            if(owner is not("S-1-5-18" or "S-1-5-32-544"))throw new IOException("Untrusted pre-existing tools directory; inspect it manually");
        }
        Directory.CreateDirectory(path);var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
        var admins=new SecurityIdentifier("S-1-5-32-544");var system=new SecurityIdentifier("S-1-5-18");var self=WindowsIdentity.GetCurrent().User!;
        foreach(var sid in privateUser?new[]{self,system}:new[]{admins,system})acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        if(!privateUser)acl.AddAccessRule(new FileSystemAccessRule(self,FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        acl.SetOwner(privateUser?self:admins);new DirectoryInfo(path).SetAccessControl(acl);
    }
    string CatalogPath=>Path.Combine(ProtectedFolder,WindowsIdentity.GetCurrent().User!.Value+".catalog.json");
    public VmProfileCatalog Catalog()
    {
        NotReparse(CatalogPath);
        if(File.Exists(CatalogPath)){var c=JsonSerializer.Deserialize<VmProfileCatalog>(File.ReadAllText(CatalogPath))??throw new InvalidDataException("Empty catalog");c.Validate();return c;}
        NotReparse(ConfigPath);if(!File.Exists(ConfigPath))return VmProfileCatalog.Empty;
        var legacy=JsonSerializer.Deserialize<BackendProfile>(File.ReadAllText(ConfigPath))??throw new InvalidDataException("Legacy profile invalid");legacy.Validate();
        // Deterministic import ID, without mutating files during read-only load.
        string id=new Guid(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(ConfigPath)).AsSpan(0,16)).ToString("D");
        return new(id,new[]{new VmProfileEntry(id,legacy)});
    }
    public BackendProfile? Load(){var c=Catalog();if(selectedProfileId!=""&&c.ActiveId!=selectedProfileId)throw new InvalidOperationException("Active profile changed elsewhere; no VM operation performed. Select a profile again.");return c.Active;}
    VmProfileCatalog MutateCatalog(Func<VmProfileCatalog,VmProfileCatalog> mutate)
    {
        if(!Admin)throw new InvalidOperationException("Run ElasticMem as Administrator to save protected backend profiles. No automatic elevation.");
        ProtectDirectory(ProtectedFolder,false);ProtectDirectory(PrivateFolder,true);NotReparse(CatalogPath);
        string lockPath=Path.Combine(ProtectedFolder,WindowsIdentity.GetCurrent().User!.Value+".lock");NotReparse(lockPath);using var lease=File.Open(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var catalog=mutate(Catalog());catalog.Validate();
        string tmp=Path.Combine(ProtectedFolder,"catalog-"+Guid.NewGuid().ToString("N")+".tmp");
        try{File.WriteAllText(tmp,JsonSerializer.Serialize(catalog));File.Move(tmp,CatalogPath,true);return catalog;}finally{if(File.Exists(tmp))File.Delete(tmp);}
    }
    public void Save(BackendProfile profile){var updated=MutateCatalog(c=>{if(selectedProfileId!=""&&c.ActiveId!=selectedProfileId)throw new InvalidOperationException("Profile changed elsewhere; save rejected");return c.Update(profile);});selectedProfileId=updated.ActiveId;}
    public void NewProfile(BackendProfile profile){selectedProfileId=MutateCatalog(c=>c.Add(profile)).ActiveId;}
    public void SelectProfile(string id){selectedProfileId=MutateCatalog(c=>c.Select(id)).ActiveId;}
    string PrepareWorker()
    {
        if(!Admin)throw new InvalidOperationException("HOST_ADMIN_REQUIRED");
        ProtectDirectory(ProtectedFolder,false);ProtectDirectory(PrivateFolder,true);
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("ElasticMem.Backend.Worker")??throw new IOException("Worker resource missing");
        using var reader=new StreamReader(stream);string script=reader.ReadToEnd();string path=Path.Combine(ProtectedFolder,"Worker.ps1");
        NotReparse(path);File.WriteAllText(path,script,new UTF8Encoding(true));
        if(File.ReadAllText(path)!=script)throw new IOException("Worker integrity check failed");return path;
    }
    public async Task<JsonElement> Run(string operation,int pid=0,long ticks=0,int session=-1)
    {
        if(operation is not("Probe" or "SaveCredential" or "Status" or "StartVm" or "ShutdownVm" or "ConfigureMemory" or "EnableCompression" or "PublishApps" or "StopProcess"))throw new ArgumentException("Operation outside allowlist");
        await gate.WaitAsync();string? requestConfig=null;
        try
        {
            using var operationLock=File.Open(Path.Combine(ProtectedFolder,WindowsIdentity.GetCurrent().User!.Value+".lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            var profile=Load()??throw new InvalidOperationException("Save backend profile first");profile.Validate();
            if(profile.BackendKind==VmBackendKind.Sys)
            {
                if(operation is not("Probe" or "Status"))VmBackendPolicy.RequireHyperV(profile.BackendKind);
                var sys=await Task.Run(()=>SysBackend.Probe(profile));Append(new{schema="elasticmem.sys.backend.event.v1",utc=DateTime.UtcNow,operation,result=sys});return sys;
            }
            VmBackendPolicy.RequireHyperV(profile.BackendKind);string script=PrepareWorker();
            requestConfig=Path.Combine(ProtectedFolder,"request-"+Guid.NewGuid().ToString("N")+".json");
            File.WriteAllText(requestConfig,JsonSerializer.Serialize(profile),new UTF8Encoding(false));
            var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=operation!="SaveCredential",RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
            foreach(var a in new[]{"-NoLogo","-NoProfile","-File",script,"-Operation",operation,"-ConfigPath",requestConfig,"-CredentialPath",CredentialFor(profile),"-TargetPid",pid.ToString(),"-StartTicks",ticks.ToString(),"-SessionId",session.ToString()})start.ArgumentList.Add(a);
            using var child=Process.Start(start)??throw new IOException("Worker start failed");
            var stdout=ReadBounded(child.StandardOutput,256*1024);var stderr=ReadBounded(child.StandardError,64*1024);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(operation=="SaveCredential"?180:90));
            try{await child.WaitForExitAsync(timeout.Token);}
            catch(OperationCanceledException){try{child.Kill();}catch{}Append(new{schema="elasticmem.backend.event.v1",utc=DateTime.UtcNow,operation,status="timeout_outcome_unknown"});throw new TimeoutException("Worker timed out; guest operation outcome unknown. Refresh status before retrying.");}
            string text=await stdout;await stderr; // Never write raw stderr/credentials to logs.
            JsonElement result;
            try{result=JsonDocument.Parse(text.Trim()).RootElement.Clone();}
            catch{Append(new{schema="elasticmem.backend.event.v1",utc=DateTime.UtcNow,operation,status="worker_protocol_failed",exit_code=child.ExitCode});throw new InvalidDataException("Worker returned no valid JSON. Check local PowerShell policy/prerequisites; no policy was bypassed.");}
            Append(new{schema="elasticmem.backend.event.v1",utc=DateTime.UtcNow,operation,result});
            return result;
        }
        finally{if(requestConfig!=null)try{File.Delete(requestConfig);}catch{}gate.Release();}
    }
    public async Task<VmCreationPlan> HashCreationMedia(VmCreationPlan plan)
    {
        plan.Validate();NotReparse(plan.MediaPath);
        string digest=await Task.Run(()=>{using var stream=new FileStream(plan.MediaPath,FileMode.Open,FileAccess.Read,FileShare.Read);return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();});
        return plan with{MediaSha256=digest};
    }
    public async Task<JsonElement> RunCreation(string operation,VmCreationPlan? plan=null)
    {
        VmBackendPolicy.RequireHyperV((Load()??throw new InvalidOperationException("Create/select a backend profile first")).BackendKind);
        if(operation is not("HostInfo" or "CreateVm"))throw new ArgumentException("Creation operation outside allowlist");
        if(operation=="CreateVm")(plan??throw new ArgumentNullException(nameof(plan))).Validate();
        await gate.WaitAsync();string? request=null;
        try
        {
            if(!Admin)throw new InvalidOperationException("Запустите ElasticMem от администратора. Hyper-V/boot/security автоматически не включаются.");
            ProtectDirectory(ProtectedFolder,false);ProtectDirectory(PrivateFolder,true);
            string lockPath=Path.Combine(ProtectedFolder,WindowsIdentity.GetCurrent().User!.Value+".lock");NotReparse(lockPath);
            using var lease=File.Open(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            VmBackendPolicy.RequireHyperV((Load()??throw new InvalidOperationException("Profile missing")).BackendKind);
            using var resource=Assembly.GetExecutingAssembly().GetManifestResourceStream("ElasticMem.Backend.CreateVm")??throw new IOException("Creation resource missing");
            using var reader=new StreamReader(resource);string script=reader.ReadToEnd();string worker=Path.Combine(ProtectedFolder,"CreateVm.ps1");NotReparse(worker);
            File.WriteAllText(worker,script,new UTF8Encoding(true));if(File.ReadAllText(worker)!=script)throw new IOException("Creator integrity check failed");
            request=Path.Combine(ProtectedFolder,"create-"+Guid.NewGuid().ToString("N")+".json");File.WriteAllText(request,JsonSerializer.Serialize((object?)plan??new{BackendKind=VmBackendKind.HyperV}),new UTF8Encoding(false));
            var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
            foreach(var a in new[]{"-NoLogo","-NoProfile","-File",worker,"-Operation",operation,"-ConfigPath",request})start.ArgumentList.Add(a);
            using var child=Process.Start(start)??throw new IOException("Creator start failed");var stdout=ReadBounded(child.StandardOutput,256*1024);var stderr=ReadBounded(child.StandardError,64*1024);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(operation=="CreateVm"?3600:90));
            try{await child.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){try{child.Kill();}catch{}Append(new{schema="elasticmem.vm.creation.event.v1",utc=DateTime.UtcNow,operation,status="timeout_outcome_unknown",creation_id=plan?.CreationId});throw new TimeoutException("Исход неизвестен. Не нажимайте повторно: проверьте VM и creation.json в ProgramData/ElasticMemBackendVMs; созданные ресурсы не удаляются вслепую.");}
            string json=await stdout;await stderr;JsonElement result;
            try{result=JsonDocument.Parse(json.Trim()).RootElement.Clone();}catch{throw new InvalidDataException("Creator did not return JSON. Check approved PowerShell policy; no Bypass was used.");}
            Append(new{schema="elasticmem.vm.creation.event.v1",utc=DateTime.UtcNow,operation,result});return result;
        }
        finally{if(request!=null)try{File.Delete(request);}catch{}gate.Release();}
    }
    static async Task<string> ReadBounded(StreamReader reader,int limit)
    {
        var text=new StringBuilder();char[] chars=new char[4096];int n;bool overflow=false;
        while((n=await reader.ReadAsync(chars))>0){if(text.Length+n<=limit)text.Append(chars,0,n);else overflow=true;}
        if(overflow)throw new InvalidDataException("Worker output limit exceeded");return text.ToString();
    }
    void Append(object value)
    {
        string line=JsonSerializer.Serialize(value,new JsonSerializerOptions(ResearchRun.Json){WriteIndented=false})+"\n";
        if(File.Exists(LogPath)&&new FileInfo(LogPath).Length+Encoding.UTF8.GetByteCount(line)>8L*1024*1024)File.Move(LogPath,Path.Combine(PrivateFolder,"events.previous.jsonl"),true);
        File.AppendAllText(LogPath,line);
    }
    public void LaunchRemoteApp(string app,JsonElement verifiedStatus)
    {
        var p=Load()??throw new InvalidOperationException("Pair a VM first");VmBackendPolicy.RequireHyperV(p.BackendKind);
        if(verifiedStatus.GetProperty("vm_id").GetString()!=p.VmId||verifiedStatus.GetProperty("vm_state").GetString()!="Running"||!verifiedStatus.GetProperty("rdp_target_matches_vm").GetBoolean())throw new InvalidOperationException("RDP endpoint not verified against this running guest. Fix DNS/network/profile, then refresh.");
        string data=p.RdpFile(app);string file=Path.Combine(PrivateFolder,BackendProfile.Alias(app)+".rdp");NotReparse(file);File.WriteAllText(file,data,Encoding.Unicode);
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"mstsc.exe")){UseShellExecute=false};start.ArgumentList.Add(file);Process.Start(start);
        Append(new{schema="elasticmem.backend.event.v1",utc=DateTime.UtcNow,operation="LaunchRemoteApp",app,status="rdp_client_started_not_guest_process_confirmation",vm_id=p.VmId});
    }
    public string Export(JsonElement? baseline,JsonElement? current)
    {
        string root=Path.Combine(AppSettings.Dir,"backend-reports");using var run=new ResearchRun(root,"backend-monitor",Updater.Current.ToString(),ResearchJobsCommit(),new{scope="backend status only; active type is explicit",backend_type=(int?)Load()?.BackendKind,custom_vm_pager_complete=false});
        if(current.HasValue)run.WriteJson("result.json",current.Value);else run.WriteJson("result.json",new{status="no_backend_measurement",physical_ram_saved_bytes=(long?)null});
        if(baseline.HasValue&&current.HasValue)try{run.WriteJson("memory-comparison.json",BackendMeasurements.Compare(baseline.Value,current.Value));}catch{run.Log("Baseline identity/boot/state invalid; no comparison produced.");}
        if(File.Exists(LogPath)&&new FileInfo(LogPath).Length<=8L*1024*1024)File.Copy(LogPath,run.FilePath("backend-events.jsonl"));
        run.Seal("backend_observation_not_compression_causal_proof","Type1 status is a SYS store probe, NOT a running VM. Type2 guest observations are Hyper-V/Windows compression, NOT our SYS pager. No physical RAM savings claim.");
        ResearchRun.Verify(run.DirectoryPath);return ResearchRun.ExportLogs(root,Path.Combine(AppSettings.Dir,"ElasticMem-backend-logs-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")+".zip"),false);
    }
    static string ResearchJobsCommit()=>Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(x=>x.Key=="CodeCommit")?.Value??"unknown";
}
