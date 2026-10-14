using System.Text.Json;
namespace ElasticMem.Core.Backend;
public static class VmCreationOutcome
{
    public static BackendProfile Accept(VmCreationPlan plan,JsonElement result,string collection,bool autoStart)
    {
        plan.Validate();
        string? S(string field)=>result.GetProperty(field).GetString();
        if(S("status")!="created_running_guest_unverified"||S("vm_state")!="Running"||(!Guid.TryParseExact(S("creation_id"),"D",out var transaction)||transaction!=Guid.Parse(plan.CreationId))||S("vm_name")!=plan.VmName||!Guid.TryParseExact(S("vm_id"),"D",out var id)||id==Guid.Empty)throw new InvalidDataException("VM creation/identity not confirmed");
        if(result.GetProperty("generation").GetInt32()!=2||S("secure_boot")!="On"||!result.GetProperty("vtpm_enabled").GetBoolean()||!result.GetProperty("private_copy_verified").GetBoolean()||result.GetProperty("source_modified").GetBoolean()||result.GetProperty("network_attached").GetBoolean()!=(plan.SwitchId!=""))throw new InvalidDataException("VM safety postconditions not confirmed");
        if(S("media_kind")!=plan.MediaKind||!string.Equals(S("media_sha256"),plan.MediaSha256,StringComparison.OrdinalIgnoreCase)||result.GetProperty("custom_sys_pager").GetBoolean())throw new InvalidDataException("Media/backend creation mismatch");
        var profile=new BackendProfile(plan.VmName,"unconfigured.invalid",string.IsNullOrWhiteSpace(collection)?"ElasticMem":collection,plan.MinimumMiB,plan.StartupMiB,plan.MaximumMiB,plan.MemoryBufferPercent,id.ToString("D"),autoStart);profile.Validate();return profile;
    }
}
public static class VmCredentialScope
{
    public static string FileName(string vmId)=>string.IsNullOrEmpty(vmId)?"guest-unpaired-credential.clixml":"guest-"+Guid.Parse(vmId).ToString("D")+"-credential.clixml";
}
