# Read-only capability check. No feature/security/VM changes; no credentials read.
$ErrorActionPreference='Stop'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$admin=([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$hv= [bool](Get-Module -ListAvailable Hyper-V)
$os=Get-CimInstance Win32_OperatingSystem
$cpu=Get-CimInstance Win32_Processor|Select-Object -First 1
[pscustomobject]@{schema='elasticmem.backend.preflight.v1';os=$os.Caption;admin=$admin;hyperv_module_present=$hv;hypervisor_present=(Get-CimInstance Win32_ComputerSystem).HypervisorPresent;virtualization_firmware_enabled=$cpu.VirtualizationFirmwareEnabled;nested_virtualization_if_host_is_vm='must be supplied by parent host; never disable protections to force it';remoteapp_client_present=(Test-Path "$env:WINDIR\System32\mstsc.exe");custom_driver_pager_implemented=$false;windows_backend_end_to_end_execution_here=$false}|ConvertTo-Json
if(-not $admin -or -not $hv){exit 1}
