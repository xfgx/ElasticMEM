namespace ElasticMem.Core.Backend;
public enum VmBackendKind { Sys=1,HyperV=2 }
public static class VmBackendPolicy
{
    public static void Validate(VmBackendKind kind){if(kind is not(VmBackendKind.Sys or VmBackendKind.HyperV))throw new ArgumentException("Unknown VM backend; no automatic fallback");}
    public static void RequireHyperV(VmBackendKind kind){Validate(kind);if(kind!=VmBackendKind.HyperV)throw new NotSupportedException("Тип1 — наш SYS. Hyper-V вызываться не будет. Собственный Windows-VMM ещё не реализован; запуск VM недоступен.");}
}
public sealed record VmProfileEntry(string Id,BackendProfile Profile);
public sealed record VmProfileCatalog(string ActiveId,IReadOnlyList<VmProfileEntry> Entries)
{
    public static VmProfileCatalog Empty=>new("",Array.Empty<VmProfileEntry>());
    public void Validate()
    {
        if(Entries.Count>32||Entries.Any(e=>!Guid.TryParseExact(e.Id,"D",out var id)||id==Guid.Empty)||Entries.Select(e=>e.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=Entries.Count)throw new InvalidDataException("Invalid/duplicate VM profile catalog");
        foreach(var entry in Entries)entry.Profile.Validate();
        if(Entries.Count==0?ActiveId!="":!Entries.Any(e=>e.Id==ActiveId))throw new InvalidDataException("Active VM profile missing");
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public BackendProfile? Active=>Entries.SingleOrDefault(e=>e.Id==ActiveId)?.Profile;
    public VmProfileCatalog Add(BackendProfile profile)
    {profile.Validate();var id=Guid.NewGuid().ToString("D");var c=new VmProfileCatalog(id,Entries.Append(new VmProfileEntry(id,profile)).ToArray());c.Validate();return c;}
    public VmProfileCatalog Select(string id){var c=this with{ActiveId=id};c.Validate();return c;}
    public VmProfileCatalog Update(BackendProfile profile)
    {
        if(Active==null)return Add(profile);
        if(profile.BackendKind!=Active.BackendKind)throw new InvalidOperationException("Backend type belongs to a profile. Create a new profile instead of silently converting an existing VM.");
        var c=this with{Entries=Entries.Select(e=>e.Id==ActiveId?e with{Profile=profile}:e).ToArray()};c.Validate();return c;
    }
}
