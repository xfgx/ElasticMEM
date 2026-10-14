# Explicit Hyper-V VM creation only. No ISO downloads, licensing/security/host-network changes.
param([Parameter(Mandatory=$true)][ValidateSet('HostInfo','CreateVm')][string]$Operation,[string]$ConfigPath)
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';$WarningPreference='SilentlyContinue'
function Fail([string]$tag){throw [InvalidOperationException]::new($tag)}
function Emit($value){[Console]::Out.WriteLine(($value|ConvertTo-Json -Depth 10 -Compress))}
function NotReparse([string]$path){
 $p=[IO.Path]::GetFullPath($path)
 while($p){if(Test-Path -LiteralPath $p){$item=Get-Item -LiteralPath $p -Force;if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){Fail 'REPARSE_PATH_REJECTED'}};$parent=[IO.Path]::GetDirectoryName($p);if($parent -eq $p){break};$p=$parent}
}
function ProtectedRoot([string]$path){
 NotReparse $path
 if(Test-Path -LiteralPath $path){$owner=(Get-Acl -LiteralPath $path).GetOwner([Security.Principal.SecurityIdentifier]).Value;if($owner -notin @('S-1-5-18','S-1-5-32-544')){Fail 'UNTRUSTED_EXISTING_VM_ROOT'}}
 else{New-Item -Path $path -ItemType Directory|Out-Null}
 $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
 foreach($s in @('S-1-5-18','S-1-5-32-544')){$sid=[Security.Principal.SecurityIdentifier]::new($s);$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
 $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'));Set-Acl -LiteralPath $path -AclObject $acl
}
$stage='preflight';$createdId=$null;$folder=$null;$source=$null;$transaction=$null;$ownsFolder=$false;$creationLock=$null
function Journal([string]$state){
 if($folder -and $ownsFolder){@{schema='elasticmem.vm.creation.journal.v1';transaction=$transaction;stage=$stage;state=$state;vm_id=$createdId;utc=[DateTime]::UtcNow.ToString('O');custom_sys_pager=$false}|ConvertTo-Json -Compress|Set-Content -LiteralPath (Join-Path $folder 'creation.json') -Encoding UTF8}
}
try{
 $who=[Security.Principal.WindowsIdentity]::GetCurrent()
 if(-not([Security.Principal.WindowsPrincipal]::new($who)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){Fail 'HOST_ADMIN_REQUIRED'}
 if(-not(Get-Module -ListAvailable Hyper-V)){Fail 'HYPERV_MISSING_NO_HOST_FEATURES_CHANGED'}
 Import-Module Hyper-V
 $service=Get-Service vmms;if($service.Status -ne 'Running'){Fail 'VMMS_NOT_RUNNING_NO_SERVICE_SETTINGS_CHANGED'}
 $machine=Get-VMHost
 if($Operation -eq 'HostInfo'){
  $switches=@(Get-VMSwitch|ForEach-Object {@{id=$_.Id.ToString();name=$_.Name;type=$_.SwitchType.ToString()}})
  Emit @{schema='elasticmem.vm.creation.host.v1';status='passed';switches=$switches;vm_names=@(Get-VM|Select-Object -ExpandProperty Name);cpu_count=[Environment]::ProcessorCount;no_host_changes=$true};exit 0
 }
 $c=Get-Content -LiteralPath $ConfigPath -Encoding UTF8 -Raw|ConvertFrom-Json
 if(-not $c.VmName -or $c.VmName.Length -gt 80 -or $c.VmName -match '[\x00-\x1f"'';`$\\/*?\[\]]'){Fail 'INVALID_VM_NAME'}
 $guid=[guid]::Empty;if(-not [guid]::TryParseExact($c.CreationId,'D',[ref]$guid) -or $guid -eq [guid]::Empty){Fail 'INVALID_CREATION_ID'};$transaction=$guid.ToString()
 if($c.MediaKind -notin @('iso','vhdx') -or $c.MediaPath -notmatch '\A[A-Za-z]:\\[^\x00-\x1f:*?"<>|]+\z' -or $c.MediaPath.Split('\') -contains '..' -or $c.MediaPath.Split('\') -contains '.' -or [IO.Path]::GetExtension($c.MediaPath) -ine ('.'+$c.MediaKind)){Fail 'LOCAL_MEDIA_PATH_REQUIRED'}
 if($c.MediaSha256 -notmatch '\A[0-9a-fA-F]{64}\z'){Fail 'MEDIA_SHA256_REQUIRED'}
 if($c.MinimumMiB -lt 1024 -or $c.MinimumMiB -gt $c.StartupMiB -or $c.StartupMiB -gt $c.MaximumMiB -or $c.MaximumMiB -gt 65536 -or $c.MemoryBufferPercent -lt 5 -or $c.MemoryBufferPercent -gt 80 -or $c.CpuCount -lt 1 -or $c.CpuCount -gt 16 -or $c.DiskGiB -lt 32 -or $c.DiskGiB -gt 512){Fail 'INVALID_RESOURCE_BUDGET'}
 if($c.CpuCount -gt [Environment]::ProcessorCount){Fail 'CPU_BUDGET_EXCEEDS_HOST'}
 $hostOs=Get-CimInstance Win32_OperatingSystem
 if([long]$hostOs.FreePhysicalMemory*1KB -lt ([long]$c.StartupMiB+1024)*1MB){Fail 'HOST_FREE_RAM_STARTUP_PLUS_1GIB_REQUIRED'}
 if(@(Get-VM|Where-Object Name -eq $c.VmName).Count){Fail 'VM_NAME_EXISTS_NO_OVERWRITE'}
 $switch=$null;if($c.SwitchId){$switch=Get-VMSwitch -Id ([guid]$c.SwitchId)}
 NotReparse $c.MediaPath
 $source=[IO.File]::Open($c.MediaPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
 $stage='media_integrity';$hash=[Security.Cryptography.SHA256]::Create()
 try{$actual=([BitConverter]::ToString($hash.ComputeHash($source))).Replace('-','').ToLowerInvariant()}finally{$hash.Dispose()}
 if($actual -ne $c.MediaSha256.ToLowerInvariant()){Fail 'MEDIA_CHANGED_HASH_MISMATCH'};$source.Position=0
 if($c.MediaKind -eq 'vhdx'){$vhd=Get-VHD -Path $c.MediaPath;if($vhd.Attached -or $vhd.VhdFormat -ne 'VHDX' -or $vhd.VhdType -notin @('Dynamic','Fixed') -or $vhd.ParentPath){Fail 'DETACHED_SELF_CONTAINED_VHDX_REQUIRED'}}
 $root=Join-Path $env:ProgramData 'ElasticMemBackendVMs'
 $drive=[IO.DriveInfo]::new([IO.Path]::GetPathRoot($root));if($drive.DriveType -ne 'Fixed' -or $drive.DriveFormat -notin @('NTFS','ReFS')){Fail 'LOCAL_NTFS_REFS_STORAGE_REQUIRED'}
 $diskBudget=$(if($c.MediaKind -eq 'iso'){$source.Length+[long]$c.DiskGiB*1GB}else{[Math]::Max($source.Length,[long]$vhd.Size)})
 if($drive.AvailableFreeSpace -lt $diskBudget+4GB){Fail 'INSUFFICIENT_STORAGE_FOR_DISK_CAPACITY_PLUS_4GIB'}
 ProtectedRoot $root;NotReparse (Join-Path $root 'creation.global.lock');$creationLock=[IO.File]::Open((Join-Path $root 'creation.global.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
 if(@(Get-VM|Where-Object Name -eq $c.VmName).Count){Fail 'VM_NAME_EXISTS_NO_OVERWRITE'}
 $folder=Join-Path $root $transaction
 if(Test-Path -LiteralPath $folder){Fail 'CREATION_ID_ALREADY_HAS_ARTIFACTS_NO_OVERWRITE'}
 New-Item -Path $folder -ItemType Directory|Out-Null;$ownsFolder=$true;Journal 'creating'
 $stage='copy_media';Journal 'creating'
 $media=Join-Path $folder ('media.'+$c.MediaKind);$copy=[IO.File]::Open($media,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
 try{$source.CopyTo($copy,1MB);$copy.Flush($true)}finally{$copy.Dispose()};$source.Dispose();$source=$null
 if((Get-FileHash -LiteralPath $media -Algorithm SHA256).Hash.ToLowerInvariant() -ne $c.MediaSha256.ToLowerInvariant()){Fail 'PRIVATE_COPY_HASH_MISMATCH'}
 $disk=$media
 if($c.MediaKind -eq 'iso'){$disk=Join-Path $folder 'system.vhdx';New-VHD -Path $disk -Dynamic -SizeBytes ([long]$c.DiskGiB*1GB)|Out-Null}
 else{$vhd=Get-VHD -Path $disk;if($vhd.Attached -or $vhd.ParentPath -or $vhd.VhdType -notin @('Fixed','Dynamic')){Fail 'COPIED_VHDX_REJECTED'}}
 $stage='register_vm';Journal 'creating'
 $newArgs=@{Name=$c.VmName;Generation=2;MemoryStartupBytes=[long]$c.StartupMiB*1MB;VHDPath=$disk;Path=$folder;ErrorAction='Stop'}
 if($switch){$newArgs.SwitchName=$switch.Name}
 $vm=New-VM @newArgs;$createdId=$vm.Id.ToString();Journal 'creating'
 Set-VM -VM $vm -Notes ('ElasticMemCreateV1:'+ $transaction) -AutomaticCheckpointsEnabled $false
 Set-VMProcessor -VM $vm -Count $c.CpuCount
 Set-VMMemory -VM $vm -DynamicMemoryEnabled $true -MinimumBytes ([long]$c.MinimumMiB*1MB) -StartupBytes ([long]$c.StartupMiB*1MB) -MaximumBytes ([long]$c.MaximumMiB*1MB) -Buffer $c.MemoryBufferPercent
 $stage='configure_secureboot_vtpm';Journal 'creating'
 Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows
 Set-VMKeyProtector -VM $vm -NewLocalKeyProtector|Out-Null
 Enable-VMTPM -VM $vm|Out-Null
 if($c.MediaKind -eq 'iso'){$dvd=Add-VMDvdDrive -VM $vm -Path $media -Passthru;Set-VMFirmware -VM $vm -FirstBootDevice $dvd}
 $stage='verify_configuration';Journal 'creating'
 $vm=Get-VM -Id ([guid]$createdId);$memory=Get-VMMemory -VM $vm;$firmware=Get-VMFirmware -VM $vm;$security=Get-VMSecurity -VM $vm;$processor=Get-VMProcessor -VM $vm
 if(@(Get-VM|Where-Object Name -eq $c.VmName).Count -ne 1){Fail 'VM_NAME_COLLISION_AFTER_CREATION'}
 if($vm.Generation -ne 2 -or $vm.Notes -ne ('ElasticMemCreateV1:'+ $transaction) -or -not $memory.DynamicMemoryEnabled -or $firmware.SecureBoot -ne 'On' -or -not $security.TpmEnabled -or $processor.Count -ne $c.CpuCount -or $memory.Minimum -ne ([long]$c.MinimumMiB*1MB) -or $memory.Startup -ne ([long]$c.StartupMiB*1MB) -or $memory.Maximum -ne ([long]$c.MaximumMiB*1MB) -or $memory.Buffer -ne $c.MemoryBufferPercent){Fail 'VM_CONFIGURATION_NOT_CONFIRMED'}
 $adapter=Get-VMNetworkAdapter -VM $vm
 if(-not $switch -and @($adapter|Where-Object {$_.SwitchId -and $_.SwitchId -ne [guid]::Empty}).Count){Fail 'UNEXPECTED_NETWORK_ATTACHMENT'}
 if($switch){if(@($adapter|Where-Object SwitchId -eq $switch.Id).Count -ne 1){Fail 'SWITCH_IDENTITY_CHANGED'}}
 $stage='start_vm';Journal 'creating';Start-VM -VM $vm|Out-Null
 $vm=Get-VM -Id ([guid]$createdId);if($vm.State -ne 'Running'){Fail 'VM_START_NOT_CONFIRMED'}
 $stage='created_running';Journal 'created_running_guest_unverified'
 Emit @{schema='elasticmem.vm.creation.result.v1';status='created_running_guest_unverified';creation_id=$transaction;vm_id=$createdId;vm_name=$vm.Name;vm_state=$vm.State.ToString();generation=2;secure_boot='On';vtpm_enabled=$security.TpmEnabled;cpu_count=$c.CpuCount;dynamic_memory=$true;private_copy_verified=$true;source_modified=$false;network_attached=[bool]$switch;storage_directory=$folder;media_kind=$c.MediaKind;media_sha256=$c.MediaSha256;os_installation=$(if($c.MediaKind -eq 'iso'){'installer_booted_user_setup_required'}else{'prepared_image_booted_os_readiness_not_verified'});rds_configured_by_creation=$false;new_sys_installed=$false;custom_sys_pager=$false;physical_ram_saved_bytes=$null};exit 0
}catch{
 $safe=$(if($_.FullyQualifiedErrorId -match '\A[A-Z0-9_]{1,100}(,|$)'){$_.FullyQualifiedErrorId.Split(',')[0]}else{'WINDOWS_VM_CREATION_REJECTED'})
 try{Journal 'failed_artifacts_retained_no_automatic_delete'}catch{}
 Emit @{schema='elasticmem.vm.creation.result.v1';status='failed';stage=$stage;creation_id=$transaction;vm_id=$createdId;storage_directory=$folder;error_id=$safe;error_class=$_.Exception.GetType().Name;raw_error_omitted=$true;artifacts_retained=$ownsFolder;no_force_shutdown=$true;no_existing_vm_overwrite=$true;custom_sys_pager=$false};exit 1
}finally{if($source){$source.Dispose()};if($creationLock){$creationLock.Dispose()}}
