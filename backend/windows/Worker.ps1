# Windows PowerShell 5.1. Fixed allowlisted operations; no arbitrary remote shell.
param(
 [Parameter(Mandatory=$true)][ValidateSet('Probe','SaveCredential','Status','StartVm','ShutdownVm','ConfigureMemory','EnableCompression','PublishApps','StopProcess')][string]$Operation,
 [Parameter(Mandatory=$true)][string]$ConfigPath,
 [Parameter(Mandatory=$true)][string]$CredentialPath,
 [int]$TargetPid=0,[long]$StartTicks=0,[int]$SessionId=-1
)
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';$WarningPreference='SilentlyContinue'
function Fail([string]$tag){throw [InvalidOperationException]::new($tag)}
function Emit($value){[Console]::Out.WriteLine(($value|ConvertTo-Json -Depth 12 -Compress))}
try {
 $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
 if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){Fail 'HOST_ADMIN_REQUIRED'}
 if(-not(Get-Module -ListAvailable Hyper-V)){Fail 'HYPERV_MODULE_MISSING_NO_SECURITY_SETTINGS_CHANGED'}
 Import-Module Hyper-V
 $c=Get-Content -LiteralPath $ConfigPath -Encoding UTF8 -Raw|ConvertFrom-Json
 if(-not $c.VmName -or $c.VmName.Length -gt 80 -or $c.VmName -match '[\x00-\x1f"'';`$\\/*?\[\]]'){Fail 'INVALID_VM_NAME'}
 if($c.RdpHost -notmatch '\A[a-zA-Z0-9](?:[a-zA-Z0-9.-]{0,251}[a-zA-Z0-9])?\z' -or $c.RdpHost.Contains('..')){Fail 'INVALID_RDP_HOST'}
 if(-not $c.CollectionName -or $c.CollectionName.Length -gt 80 -or $c.CollectionName -match '[\x00-\x1f]'){Fail 'INVALID_COLLECTION'}
 if($c.MinimumMiB -lt 1024 -or $c.MinimumMiB -gt $c.StartupMiB -or $c.StartupMiB -gt $c.MaximumMiB -or $c.MaximumMiB -gt 65536 -or $c.MemoryBufferPercent -lt 5 -or $c.MemoryBufferPercent -gt 80){Fail 'INVALID_MEMORY_BUDGET'}
 $selection=@(Get-VM -Name $c.VmName)
 if($selection.Count -ne 1){Fail 'EXACTLY_ONE_VM_REQUIRED'}
 $vm=$selection[0]
 if($c.VmId -and ([guid]$c.VmId -ne $vm.Id)){Fail 'VM_IDENTITY_CHANGED'}
 if($Operation -eq 'Probe'){
  Emit @{schema='elasticmem.backend.worker.v1';status='passed';operation=$Operation;vm_id=$vm.Id.ToString();vm_state=$vm.State.ToString();dynamic_memory=(Get-VMMemory -VM $vm).DynamicMemoryEnabled;memory_backend='Windows guest compression + Hyper-V Dynamic Memory';custom_driver_pager_implemented=$false};exit 0
 }
 if(-not $c.VmId){Fail 'PAIR_VM_ID_BEFORE_ACTIONS'}
 if($Operation -eq 'SaveCredential'){
  $cred=Get-Credential -Message 'Admin account INSIDE this backend Windows VM. Stored with Windows user/machine DPAPI. Never added to logs.'
  if(-not $cred){Fail 'CREDENTIAL_PROMPT_CANCELLED'}
  $cred|Export-Clixml -LiteralPath $CredentialPath
  $acl=[Security.AccessControl.FileSecurity]::new();$acl.SetAccessRuleProtection($true,$false)
  $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User
  foreach($id in @($sid,[Security.Principal.SecurityIdentifier]::new('S-1-5-18'))){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($id,'FullControl','Allow'))}
  Set-Acl -LiteralPath $CredentialPath -AclObject $acl
  Emit @{schema='elasticmem.backend.worker.v1';status='passed';operation=$Operation;credential_storage='Windows DPAPI CLI XML, current user and machine; no password returned'};exit 0
 }
 if($Operation -eq 'StartVm'){if($vm.State -eq 'Off'){Start-VM -VM $vm|Out-Null}elseif($vm.State -ne 'Running'){Fail 'VM_STATE_NOT_OFF_OR_RUNNING'};Emit @{schema='elasticmem.backend.worker.v1';status='passed';operation=$Operation;vm_id=$vm.Id.ToString()};exit 0}
 if($Operation -eq 'ConfigureMemory'){
  if($vm.State -ne 'Off'){Fail 'MEMORY_CONFIGURATION_REQUIRES_VM_OFF'}
  Set-VMMemory -VM $vm -DynamicMemoryEnabled $true -MinimumBytes ([long]$c.MinimumMiB*1MB) -StartupBytes ([long]$c.StartupMiB*1MB) -MaximumBytes ([long]$c.MaximumMiB*1MB) -Buffer $c.MemoryBufferPercent
  $memory=Get-VMMemory -VM $vm
  if(-not $memory.DynamicMemoryEnabled){Fail 'DYNAMIC_MEMORY_NOT_ENABLED'}
  Emit @{schema='elasticmem.backend.worker.v1';status='passed';operation=$Operation;vm_id=$vm.Id.ToString();dynamic_memory=$true};exit 0
 }
 if($Operation -eq 'ShutdownVm'){
  # Guest shutdown, not TurnOff/force. It can fail if integration service is unavailable.
  Stop-VM -VM $vm -Confirm:$false
  Emit @{schema='elasticmem.backend.worker.v1';status='passed';operation=$Operation;vm_id=$vm.Id.ToString()};exit 0
 }
 if($vm.State -ne 'Running'){Fail 'VM_MUST_BE_RUNNING'}
 if(-not(Test-Path -LiteralPath $CredentialPath)){Fail 'SAVE_GUEST_CREDENTIAL_FIRST'}
 $cred=Import-Clixml -LiteralPath $CredentialPath
 if($cred -isnot [Management.Automation.PSCredential]){Fail 'INVALID_CREDENTIAL_FILE'}
 $guest=Invoke-Command -VMId $vm.Id -Credential $cred -ArgumentList $Operation,$c.CollectionName,$TargetPid,$StartTicks,$SessionId -ScriptBlock {
  param($op,$collection,$targetPid,$expectedTicks,$expectedSession)
  $ErrorActionPreference='Stop';$WarningPreference='SilentlyContinue';$ProgressPreference='SilentlyContinue'
  function Reject($tag){throw [InvalidOperationException]::new($tag)}
  $os=Get-CimInstance Win32_OperatingSystem
  if($op -eq 'EnableCompression'){
   if(-not(Get-Command Enable-MMAgent -ErrorAction SilentlyContinue)){Reject 'GUEST_MEMORY_COMPRESSION_API_UNAVAILABLE'}
   Enable-MMAgent -MemoryCompression
   $enabled=(Get-MMAgent).MemoryCompression
   if($enabled -ne $true){Reject 'GUEST_MEMORY_COMPRESSION_NOT_ENABLED'}
   return @{status='passed';guest_computer=$env:COMPUTERNAME;memory_compression_enabled=$true;host_compression_policy_changed=$false;custom_driver_used=$false}
  }
  if($op -eq 'PublishApps'){
   if($os.Caption -notmatch 'Windows Server'){Reject 'SUPPORTED_REMOTEAPP_REQUIRES_WINDOWS_SERVER_RDS_NO_REGISTRY_PATCHES'}
   if(-not(Get-Command Get-RDSessionCollection -ErrorAction SilentlyContinue)){Reject 'RDS_DEPLOYMENT_COLLECTION_LICENSES_MUST_BE_CONFIGURED_FIRST'}
   $broker=([Net.Dns]::GetHostEntry($env:COMPUTERNAME)).HostName
   $found=Get-RDSessionCollection -ConnectionBroker $broker|Where-Object CollectionName -eq $collection
   if(-not $found){Reject 'RDS_COLLECTION_NOT_FOUND'}
   $sessionHosts=@(Get-RDSessionHost -CollectionName $collection -ConnectionBroker $broker)
   if($sessionHosts.Count -ne 1 -or ($sessionHosts[0].SessionHost.Split('.')[0] -ine $env:COMPUTERNAME)){Reject 'ONLY_SINGLE_GUEST_SESSION_HOST_SUPPORTED'}
   $apps=@(
    @{alias='ElasticMem-Edge';name='ElasticMem Edge';basename='msedge.exe';publisher='Microsoft Corporation';paths=@("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe","$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe")},
    @{alias='ElasticMem-Chrome';name='ElasticMem Chrome';basename='chrome.exe';publisher='Google (LLC|Inc)';paths=@("$env:ProgramFiles\Google\Chrome\Application\chrome.exe","${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe")},
    @{alias='ElasticMem-Telegram';name='ElasticMem Telegram';basename='Telegram.exe';publisher='Telegram';paths=@("$env:ProgramFiles\Telegram Desktop\Telegram.exe","${env:ProgramFiles(x86)}\Telegram Desktop\Telegram.exe")}
   );$rows=@()
   foreach($app in $apps){
    $path=$app.paths|Where-Object {Test-Path -LiteralPath $_ -PathType Leaf}|Select-Object -First 1
    if(-not $path){$rows+=@{alias=$app.alias;status='not_installed_system_wide'};continue}
    $sig=Get-AuthenticodeSignature -LiteralPath $path
    if($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch $app.publisher){$rows+=@{alias=$app.alias;status='signature_or_publisher_rejected'};continue}
    $existing=Get-RDRemoteApp -CollectionName $collection -ConnectionBroker $broker|Where-Object Alias -eq $app.alias
    if($existing -and $existing.FilePath -ne $path){Reject 'REMOTEAPP_ALIAS_COLLISION'}
    if(-not $existing){New-RDRemoteApp -CollectionName $collection -ConnectionBroker $broker -Alias $app.alias -DisplayName $app.name -FilePath $path -CommandLineSetting DoNotAllow|Out-Null}
    $rows+=@{alias=$app.alias;status='published_verified'}
   }
   return @{status= $(if(@($rows|Where-Object status -eq 'published_verified').Count -eq 3){'passed'}else{'partial'});guest_computer=$env:COMPUTERNAME;rows=$rows;rdp_requires_user_authentication=$true}
  }
  if($op -eq 'StopProcess'){
   if($targetPid -le 0 -or $expectedTicks -le 0 -or $expectedSession -le 0){Reject 'PROCESS_IDENTITY_REQUIRED_INTERACTIVE_SESSION_ONLY'}
   $p=Get-Process -Id $targetPid
   $heldHandle=$p.Handle # Keep a stable process handle before checking lifetime and killing.
   if($p.ProcessName -notin @('msedge','chrome','Telegram') -or $p.StartTime.ToUniversalTime().Ticks -ne $expectedTicks -or $p.SessionId -ne $expectedSession){Reject 'PROCESS_IDENTITY_CHANGED_OR_NOT_ALLOWLISTED'}
   # Stop exactly the guest process handle, never host PID or process-tree bulk kill.
   $p.Kill();if(-not $p.WaitForExit(5000)){Reject 'PROCESS_EXIT_NOT_CONFIRMED'}
   return @{status='passed';guest_computer=$env:COMPUTERNAME;stopped_guest_pid=$targetPid;start_ticks=$expectedTicks;session_id=$expectedSession}
  }
  if($op -ne 'Status'){Reject 'UNSUPPORTED_OPERATION'}
  $rows=@();$unreadable=0
  foreach($p in Get-Process -Name msedge,chrome,Telegram -ErrorAction SilentlyContinue){
   try{$rows+=@{pid=$p.Id;name=$p.ProcessName;session_id=$p.SessionId;start_ticks=$p.StartTime.ToUniversalTime().Ticks;working_set_bytes=$p.WorkingSet64;private_commit_bytes=$p.PrivateMemorySize64;cpu_total_ms=$p.TotalProcessorTime.TotalMilliseconds}}catch{$unreadable++}
  }
  $mma=$null;try{$mma=(Get-MMAgent).MemoryCompression}catch{}
  $storeWs=$null;try{$storeWs=[long]((Get-Process -Name MemCompression -ErrorAction Stop|Measure-Object WorkingSet64 -Sum).Sum)}catch{}
  return @{status='passed';guest_computer=$env:COMPUTERNAME;guest_boot_id=$os.LastBootUpTime.ToUniversalTime().ToString('O');guest_os=$os.Caption;guest_ipv4=@(Get-NetIPAddress -AddressFamily IPv4|Where-Object {$_.IPAddress -ne '127.0.0.1'}|Select-Object -ExpandProperty IPAddress);guest_total_visible_bytes=[long]$os.TotalVisibleMemorySize*1KB;guest_available_bytes=[long]$os.FreePhysicalMemory*1KB;memory_compression_enabled=$mma;compressed_store_process_ws_bytes=$storeWs;compressed_payload_bytes=$null;processes=@($rows|Select-Object -First 256);process_list_truncated=($rows.Count -gt 256);unreadable_processes=$unreadable;per_process_physical_ram_saved_bytes=$null}
 }
 $vm=Get-VM -Id $vm.Id;$memory=Get-VMMemory -VM $vm
 $hostOs=Get-CimInstance Win32_OperatingSystem
 if($Operation -eq 'Status'){
  $rdpMatches=$false;try{$rdpIps=@([Net.Dns]::GetHostAddresses($c.RdpHost)|ForEach-Object ToString);$rdpMatches=@($rdpIps|Where-Object {$_ -in $guest.guest_ipv4}).Count -gt 0}catch{}
  Emit @{schema='elasticmem.backend.status.v1';status='passed';utc=[DateTime]::UtcNow.ToString('O');vm_id=$vm.Id.ToString();vm_state=$vm.State.ToString();guest_boot_id=$guest.guest_boot_id;assigned_bytes=[long]$vm.MemoryAssigned;demand_bytes=[long]$vm.MemoryDemand;dynamic_memory=$memory.DynamicMemoryEnabled;host_available_bytes=[long]$hostOs.FreePhysicalMemory*1KB;host_total_visible_bytes=[long]$hostOs.TotalVisibleMemorySize*1KB;guest=$guest;rdp_target_matches_vm=$rdpMatches;custom_driver_pager_implemented=$false;physical_ram_saved_bytes=$null;note='MemoryAssigned/host availability are observations, not isolated causal savings attributable to compression'}
 }else{Emit @{schema='elasticmem.backend.worker.v1';operation=$Operation;status=$guest.status;vm_id=$vm.Id.ToString();guest=$guest;custom_driver_used=$false}}
}catch{
 # Do not print raw exceptions: they may carry a username, paths or remote data.
 Emit @{schema='elasticmem.backend.worker.v1';operation=$Operation;status='failed';error_class=$_.Exception.GetType().Name;error_id=$(if($_.FullyQualifiedErrorId -match '\A[A-Z0-9_]{1,100}(,|$)'){$_.FullyQualifiedErrorId.Split(',')[0]}else{'WINDOWS_OPERATION_REJECTED'});raw_error_omitted=$true;password_logged=$false};exit 1
}
