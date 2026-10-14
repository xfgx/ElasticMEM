using System.Text.RegularExpressions;
namespace ElasticMem.Core.Backend;

// Local trusted media only. Never a URL, host system disk, attached VHD or existing VM to overwrite.
public sealed record VmCreationPlan(string VmName,string MediaPath,string MediaSha256,string MediaKind,
    string SwitchId="",int CpuCount=2,int DiskGiB=64,int MinimumMiB=2048,int StartupMiB=4096,
    int MaximumMiB=8192,int MemoryBufferPercent=20,string CreationId="")
{
    public void Validate()
    {
        new BackendProfile(VmName,"unconfigured.invalid","ElasticMem",MinimumMiB,StartupMiB,MaximumMiB,MemoryBufferPercent).Validate();
        if(MediaKind is not("vhdx" or "iso"))throw new ArgumentException("Only prepared VHDX or installer ISO supported");
        if(!Regex.IsMatch(MediaPath,@"\A[A-Za-z]:\\[^\x00-\x1f:*?\""<>|]+\z")||MediaPath.Split('\\').Any(s=>s is "." or "..")||MediaPath.EndsWith('\\'))throw new ArgumentException("Local absolute drive path without ADS/device/UNC/traversal required");
        if(!MediaPath.EndsWith("."+MediaKind,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Media extension mismatch");
        if(!Regex.IsMatch(MediaSha256,@"\A[0-9a-fA-F]{64}\z"))throw new ArgumentException("Expected media SHA256 required");
        if(SwitchId!=""&&(!Guid.TryParseExact(SwitchId,"D",out var switchId)||switchId==Guid.Empty))throw new ArgumentException("Exact existing switch GUID required");
        if(!Guid.TryParseExact(CreationId,"D",out var id)||id==Guid.Empty)throw new ArgumentException("Creation transaction GUID required");
        if(CpuCount is <1 or >16||DiskGiB is <32 or >512)throw new ArgumentException("CPU1..16, new ISO disk32..512GiB");
    }
}
