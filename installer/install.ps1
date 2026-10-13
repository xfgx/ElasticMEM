# Установка ElasticMem на Windows 11
# Пример: powershell -ExecutionPolicy Bypass -File install.ps1 -Autostart -Mode all
#         powershell -ExecutionPolicy Bypass -File install.ps1 -Autostart -Mode process -Process chrome -MinIdle 60
param(
    [switch]$Autostart,
    [ValidateSet("all","process")][string]$Mode = "all",
    [string]$Process = "",
    [int]$MinIdle = 30,
    [switch]$EnableMemoryCompression
)
$ErrorActionPreference = "Stop"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $argsList = @("-NoProfile","-ExecutionPolicy","Bypass","-File","`"$PSCommandPath`"")
    foreach ($k in $PSBoundParameters.Keys) { $v = $PSBoundParameters[$k]; if ($v -is [switch]) { if ($v) { $argsList += "-$k" } } else { $argsList += "-$k"; $argsList += "`"$v`"" } }
    Start-Process powershell -Verb RunAs -ArgumentList $argsList
    exit
}

$src = Join-Path $PSScriptRoot "ElasticMem.exe"
if (-not (Test-Path $src)) { throw "Не найден ElasticMem.exe рядом со скриптом" }
$dst = Join-Path $env:ProgramFiles "ElasticMem"
New-Item -ItemType Directory -Force -Path $dst | Out-Null
Get-Process ElasticMem -ErrorAction SilentlyContinue | Stop-Process -Force
Copy-Item $src $dst -Force
Copy-Item (Join-Path $PSScriptRoot "uninstall.ps1") $dst -Force -ErrorAction SilentlyContinue
$exe = Join-Path $dst "ElasticMem.exe"

# Ярлык в меню «Пуск»
$lnk = Join-Path ([Environment]::GetFolderPath("CommonPrograms")) "ElasticMem.lnk"
$ws = New-Object -ComObject WScript.Shell
$s = $ws.CreateShortcut($lnk); $s.TargetPath = $exe; $s.WorkingDirectory = $dst; $s.Description = "ElasticMem — эластичное сжатие памяти"; $s.Save()

if ($EnableMemoryCompression) {
    Enable-MMAgent -MemoryCompression
    Write-Host "Memory Compression включено (может потребоваться перезагрузка)."
}

if ($Autostart) {
    $a = if ($Mode -eq "all") { "--agent --all --tray --min-idle $MinIdle" } else { "--agent --process $Process --tray --min-idle $MinIdle" }
    $action = New-ScheduledTaskAction -Execute $exe -Argument $a
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -RunLevel Highest -LogonType Interactive
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
    Register-ScheduledTask -TaskName "ElasticMem Agent" -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
    Write-Host "Автозапуск агента при входе: $a"
}
Write-Host "Установлено в $dst. Запуск: меню «Пуск» → ElasticMem."
Start-Process $exe
