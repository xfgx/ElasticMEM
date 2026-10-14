$ErrorActionPreference='Stop'
$who=[Security.Principal.WindowsIdentity]::GetCurrent();$name='ElasticMem-Backend-'+$who.User.Value
if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){Unregister-ScheduledTask -TaskName $name -Confirm:$false}
Write-Host 'Logon task removed. Close ElasticMem; inspect/remove ProgramFiles\ElasticMem Backend and local backend-private credentials separately. Guest VM, RDS publication, and memory settings are NOT removed or reverted automatically.'
