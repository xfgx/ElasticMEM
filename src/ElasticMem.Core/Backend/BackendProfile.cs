using System.Text.Json;
using System.Text.RegularExpressions;
namespace ElasticMem.Core.Backend;
public sealed record BackendProfile(string VmName, string RdpHost, string CollectionName,
    int MinimumMiB=2048,int StartupMiB=4096,int MaximumMiB=8192,int MemoryBufferPercent=20,
    string VmId="",bool AutoStartVm=false)
{
    public void Validate()
    {
        if(string.IsNullOrWhiteSpace(VmName)||VmName.Length>80||VmName.Any(char.IsControl)||VmName.IndexOfAny(new[]{'"','\'', ';','`','$','\\','/','*','?','[',']'})>=0)throw new ArgumentException("Invalid VM name");
        if(!Regex.IsMatch(RdpHost,@"\A[a-zA-Z0-9](?:[a-zA-Z0-9.-]{0,251}[a-zA-Z0-9])?\z")||RdpHost.Contains(".."))throw new ArgumentException("RDP host must be a hostname/IPv4 without port or control characters");
        if(string.IsNullOrWhiteSpace(CollectionName)||CollectionName.Length>80||CollectionName.Any(char.IsControl))throw new ArgumentException("RDS collection required");
        if(MinimumMiB<1024||MinimumMiB>StartupMiB||StartupMiB>MaximumMiB||MaximumMiB>65536||MemoryBufferPercent is <5 or >80)throw new ArgumentException("Require 1024 <= minimum <= startup <= maximum <=65536 MiB and buffer5..80");
        if(VmId!=""&&!Guid.TryParse(VmId,out _))throw new ArgumentException("Invalid VM identity");
    }
    public static string Alias(string app)=>app switch{"edge"=>"ElasticMem-Edge","chrome"=>"ElasticMem-Chrome","telegram"=>"ElasticMem-Telegram",_=>throw new ArgumentException("App outside allowlist")};
    public string RdpFile(string app)
    {
        Validate();string alias=Alias(app);
        // No credentials, unsigned server trust overrides or security downgrade fields.
        return $"full address:s:{RdpHost}\r\nremoteapplicationmode:i:1\r\nremoteapplicationprogram:s:||{alias}\r\nremoteapplicationname:s:ElasticMem backend - {app}\r\ndisableconnectionsharing:i:0\r\nprompt for credentials:i:1\r\nauthentication level:i:1\r\nredirectclipboard:i:0\r\nredirectprinters:i:0\r\nredirectcomports:i:0\r\nredirectsmartcards:i:0\r\ndrivestoredirect:s:\r\n";
    }
}
public static class BackendMeasurements
{
    public static object Compare(JsonElement baseline,JsonElement current)
    {
        string? a=baseline.GetProperty("vm_id").GetString(), b=current.GetProperty("vm_id").GetString();
        if(a!=b||baseline.GetProperty("guest_boot_id").GetString()!=current.GetProperty("guest_boot_id").GetString())throw new InvalidOperationException("VM identity/boot changed; baseline invalid");
        if(baseline.GetProperty("vm_state").GetString()!="Running"||current.GetProperty("vm_state").GetString()!="Running")throw new InvalidOperationException("Both checkpoints must be running");
        long before=baseline.GetProperty("assigned_bytes").GetInt64(),after=current.GetProperty("assigned_bytes").GetInt64();
        return new{schema="elasticmem.backend.memory_delta.v1",vm_id=a,assigned_delta_bytes=after-before,hyperv_assignment_reduction_bytes=before-after,
            physical_ram_saved_bytes=(long?)null,compression_causal_effect_bytes=(long?)null,
            note="Hyper-V assignment change is observed; guest compression/whole-host saving not causally isolated. No per-process unique physical RAM claim."};
    }
}
