# Открыть приложение гостя отдельным окном (RemoteApp) через проброшенный порт RDP QEMU-гостя.
#   -Rdp     : ваш готовый .rdp-файл (тот, которым вы открывали приложение из Hyper-V); адрес будет заменён
#   -Program : или путь/алиас программы в госте (например '||msedge' или 'C:\Program Files\...\app.exe')
param([string]$Rdp, [string]$Program, [int]$RdpPort = 13389, [string]$User)
. (Join-Path $PSScriptRoot 'scripts\Common.ps1')
$lines = @()
if ($Rdp) {
  $lines = Get-Content $Rdp | Where-Object { $_ -notmatch '^(full address|server port):' }
} elseif ($Program) {
  $lines = @('remoteapplicationmode:i:1', "remoteapplicationprogram:s:$Program", "remoteapplicationname:s:$(Split-Path $Program -Leaf)",
    'disableremoteappcapscheck:i:1', 'alternate shell:s:rdpinit.exe', 'prompt for credentials:i:1')
} else { throw 'Укажите -Rdp <файл.rdp> или -Program <программа в госте>' }
$lines += "full address:s:127.0.0.1:$RdpPort"
if ($User) { $lines += "username:s:$User" }
$tmp = Join-Path $Work 'app.rdp'
$lines | Set-Content $tmp -Encoding Unicode
Start-Process mstsc.exe -ArgumentList ('"' + $tmp + '"')
Write-Host "Открываю $tmp"
