using System.ComponentModel;
using System.Text.Json;
using ElasticMem.Core.Backend;
using ElasticMem.DriverClient;
namespace ElasticMem.App.Backend;
internal static class SysBackend
{
    // SYS capability probe + read-only WHP availability. No role/boot-policy change, PowerShell or fallback.
    public static JsonElement Probe(BackendProfile profile)
    {
        profile.Validate();if(profile.BackendKind!=VmBackendKind.Sys)throw new ArgumentException("SYS profile required");
        try
        {
            using var store=new KernelPageStore();var caps=store.Capabilities();var stats=store.Stats();var whp=ElasticMem.Vmm.WhpNative.Probe();
            return JsonSerializer.SerializeToElement(new{schema="elasticmem.sys.backend.v2",status="blocked_vm_runtime_missing",backend_type=1,backend="custom_WHP_SYS",whp_availability=whp,requires_windows_hypervisor_for_vmm=true,windows_boot="not_implemented",synthetic_whp_sys_runloop="implemented_not_native_tested_here",driver_protocol_reachable=true,capabilities=caps,store=stats,requested_store_capacity=profile.SysStoreCapacity,requested_store_quota=profile.SysStoreQuota,settings_applied_to_vm=false,profile_name=profile.VmName,vm_created=false,can_start_windows_vm=false,requires_hyperv_role=false,hyperv_called=false,automatic_backend_fallback=false,physical_ram_saved_bytes=(long?)null,message="SYS доступен. WHP synthetic VMM есть в исходниках, но Windows boot/UEFI/устройства не реализованы. WHP требует гипервизор Windows, без автоматического включения. Профиль не является работающей Windows-VM."});
        }
        catch(Exception ex)when(ex is Win32Exception or InvalidDataException or PlatformNotSupportedException)
        {
            return JsonSerializer.SerializeToElement(new{schema="elasticmem.sys.backend.v2",status=ex switch{Win32Exception{NativeErrorCode:2 or 3}=>"driver_not_installed_or_not_started",Win32Exception{NativeErrorCode:5}=>"access_denied_requires_elevated_admin",InvalidDataException=>"driver_protocol_unsupported_old_sys",PlatformNotSupportedException=>"requires_windows",_=>"driver_open_failed"},backend_type=1,driver_protocol_reachable=false,can_start_windows_vm=false,requires_hyperv_role=false,hyperv_called=false,automatic_backend_fallback=false,error_class=ex.GetType().Name,win32_error=ex is Win32Exception w?w.NativeErrorCode:(int?)null,physical_ram_saved_bytes=(long?)null,message=ex switch{Win32Exception{NativeErrorCode:2 or 3}=>"Устройство \\\\.\\ElasticMemStore не найдено: SYS не установлен или служба не запущена. Без SYS можно проверить WHP-путь кнопкой «WHP + user-store».",Win32Exception{NativeErrorCode:5}=>"Нет доступа к устройству: запустите ElasticMem явно от администратора (без auto-elevation).",InvalidDataException=>"Установлен старый/чужой SYS без capabilities/resize. Нужен новый доверенный SYS.",_=>"SYS недоступен."}+" Hyper-V автоматически не используется."});
        }
    }
}
