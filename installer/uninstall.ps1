$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""; exit }
Get-Process ElasticMem -ErrorAction SilentlyContinue | Stop-Process -Force
Unregister-ScheduledTask -TaskName "ElasticMem Agent" -Confirm:$false -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath("CommonPrograms")) "ElasticMem.lnk") -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:ProgramFiles "ElasticMem") -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "ElasticMem удалён."
