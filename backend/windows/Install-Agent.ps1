# Explicit user-logon agent installation. No boot/security policy changes.
param([Parameter(Mandatory=$true)][string]$ProgramDirectory,[switch]$ConfirmInstall)
$ErrorActionPreference='Stop'
if(-not $ConfirmInstall){throw 'Review package hashes and scripts. Pass -ConfirmInstall to copy this package to protected ProgramFiles and register your logon agent.'}
$who=[Security.Principal.WindowsIdentity]::GetCurrent()
if(-not([Security.Principal.WindowsPrincipal]::new($who)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Elevated Administrator required.'}
$src=(Resolve-Path -LiteralPath $ProgramDirectory).Path
if(-not(Test-Path -LiteralPath (Join-Path $src 'ElasticMem.exe'))){throw 'Missing ElasticMem.exe.'}
$dest=Join-Path $env:ProgramFiles 'ElasticMem Backend'
if(Test-Path -LiteralPath $dest){throw 'Protected destination exists; stop agent and inspect previous installation. No overwrite performed.'}
New-Item -Path $dest -ItemType Directory|Out-Null
$acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
foreach($sid in @('S-1-5-18','S-1-5-32-544')){$id=[Security.Principal.SecurityIdentifier]::new($sid);$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($id,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
$users=[Security.Principal.SecurityIdentifier]::new('S-1-5-32-545');$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($users,'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'));$acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'));Set-Acl -LiteralPath $dest -AclObject $acl
Copy-Item -LiteralPath (Join-Path $src 'ElasticMem.exe') -Destination $dest
if((Get-FileHash -LiteralPath (Join-Path $src 'ElasticMem.exe')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $dest 'ElasticMem.exe')).Hash){throw 'Copy hash mismatch.'}
$name='ElasticMem-Backend-'+$who.User.Value
if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){throw 'Agent task already exists.'}
$action=New-ScheduledTaskAction -Execute (Join-Path $dest 'ElasticMem.exe') -Argument '--backend-agent' -WorkingDirectory $dest
$trigger=New-ScheduledTaskTrigger -AtLogOn -User $who.Name
$principal=New-ScheduledTaskPrincipal -UserId $who.Name -LogonType Interactive -RunLevel Highest
$settings=New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable
Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Approved interactive ElasticMem backend agent; Guest DPAPI credentials stay with this user. No password stored in task.'|Out-Null
Write-Host 'User-logon agent registered. Log off/on or launch protected ElasticMem.exe --backend-agent. Configure/pair VM in Backend VM tab first. No host settings altered.'
