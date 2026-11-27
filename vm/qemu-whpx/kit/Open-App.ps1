# Открыть приложение гостя отдельным окном (RemoteApp) через проброшенный порт RDP QEMU-гостя.
#   -Rdp     : ваш готовый .rdp-файл (тот, которым вы открывали приложение из Hyper-V); адрес будет заменён
#   -Program : или путь/алиас программы в госте (например '||ElasticMem-Edge' или 'C:\Program Files\...\app.exe')
param([string]$Rdp, [string]$Program, [int]$RdpPort = 13389, [string]$User)
. (Join-Path $PSScriptRoot 'scripts\Common.ps1')
$lines = @()
if ($Rdp) {
  $lines = Get-Content $Rdp | Where-Object { $_ -notmatch '^(full address|server port):' }
} elseif ($Program) {
  $name = if ($Program.StartsWith('||')) { $Program.Substring(2) } else { Split-Path $Program -Leaf }
  # как BackendProfile.RdpFile (бэкенд Hyper-V): режим RemoteApp, псевдоним опубликованной программы, без
  # перенаправления дисков/принтеров/буфера; disableremoteappcapscheck — гость без роли RDS (клиентская Windows)
  $lines = @('remoteapplicationmode:i:1', "remoteapplicationprogram:s:$Program", "remoteapplicationname:s:$name",
    'disableremoteappcapscheck:i:1', 'disableconnectionsharing:i:0', 'prompt for credentials:i:1', 'authentication level:i:2',
    'redirectclipboard:i:1', 'redirectprinters:i:0', 'redirectcomports:i:0', 'redirectsmartcards:i:0', 'drivestoredirect:s:',
    'audiomode:i:0', 'connection type:i:6', 'networkautodetect:i:0', 'bandwidthautodetect:i:0', 'compression:i:0')
  if (-not $Program.StartsWith('||')) { $lines += 'alternate shell:s:rdpinit.exe' }
} else { throw 'Укажите -Rdp <файл.rdp> или -Program <программа в госте>' }
$lines += "full address:s:127.0.0.1:$RdpPort"
if ($User) { $lines += "username:s:$User" }
$tmp = Join-Path $Work 'app.rdp'
$lines | Set-Content $tmp -Encoding Unicode
Start-Process mstsc.exe -ArgumentList ('"' + $tmp + '"')
Write-Host "Открываю $tmp"
