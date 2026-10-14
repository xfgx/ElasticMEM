using System.ComponentModel;
using System.Text.Json;
using ElasticMem.Core.Backend;
using ElasticMem.DriverClient;
namespace ElasticMem.App.Backend;
internal static class SysBackend
{
    // No Hyper-V/WHP import, PowerShell, guest PID, boot policy change or automatic fallback.
    public static JsonElement Probe(BackendProfile profile)
    {
        profile.Validate();if(profile.BackendKind!=VmBackendKind.Sys)throw new ArgumentException("SYS profile required");
        try
        {
            using var store=new KernelPageStore();var caps=store.Capabilities();var stats=store.Stats();
            return JsonSerializer.SerializeToElement(new{schema="elasticmem.sys.backend.v1",status="blocked_vm_runtime_missing",backend_type=1,backend="our_SYS",driver_protocol_reachable=true,capabilities=caps,store=stats,requested_store_capacity=profile.SysStoreCapacity,requested_store_quota=profile.SysStoreQuota,settings_applied_to_vm=false,profile_name=profile.VmName,vm_created=false,can_start_windows_vm=false,requires_hyperv_role=false,hyperv_called=false,automatic_backend_fallback=false,physical_ram_saved_bytes=(long?)null,message="SYS page store доступен. Собственный Windows-VMM не реализован: профиль не является работающей VM. Доступна только отдельная проверка собственных буферов через SYS."});
        }
        catch(Exception ex)when(ex is Win32Exception or InvalidDataException or PlatformNotSupportedException)
        {
            return JsonSerializer.SerializeToElement(new{schema="elasticmem.sys.backend.v1",status="driver_unavailable_or_protocol_unsupported",backend_type=1,driver_protocol_reachable=false,can_start_windows_vm=false,requires_hyperv_role=false,hyperv_called=false,automatic_backend_fallback=false,error_class=ex.GetType().Name,win32_error=ex is Win32Exception w?w.NativeErrorCode:(int?)null,physical_ram_saved_bytes=(long?)null,message="Нужен новый доверенный SYS с capabilities/resize. Старый SYS не поддерживается этим путём. Hyper-V автоматически не используется."});
        }
    }
}
