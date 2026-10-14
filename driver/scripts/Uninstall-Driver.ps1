$ErrorActionPreference='Stop'
$principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Elevated Administrator required.' }
$service=Get-Service ElasticMemStore -ErrorAction SilentlyContinue
if (-not $service) { Write-Host 'ElasticMemStore service absent.'; return }
if ($service.Status -ne 'Stopped') {
  Write-Host 'Close ElasticMem and ALL driver clients first.'
  & sc.exe stop ElasticMemStore
  if ($LASTEXITCODE -ne 0) { throw 'Stop failed. Do not force-unload. Close clients; if necessary revert the VM snapshot.' }
  $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))
}
& sc.exe delete ElasticMemStore
if ($LASTEXITCODE -ne 0) { throw 'Service deletion failed.' }
Write-Host 'Demand-start service removed. Signed SYS retained in ProgramData\ElasticMemDriver for inspection; remove manually after confirming the driver is unloaded.'
