# Запуск Windows-гостя в QEMU с ускорителем WHPX вместо Hyper-V, чтобы память гостя принадлежала нашему процессу.
#   -Mode plain : обычный QEMU (дорожка 1: память гостя сжимает Windows, агент probe.exe вытесняет холодное)
#   -Mode pager : QEMU + прокси WinHvPlatform.dll с em-pager (дорожка 2: своё хранилище zero / dedup / XOR-delta,
#                 RAM хоста освобождается). QEMU не модифицирован: прокси кладётся рядом с qemu-system-x86_64.exe.
# Исходный диск Hyper-V НЕ меняется: все записи гостя идут в overlay.qcow2 (qemu-img -b <vhdx>).
param(
  [Parameter(Mandatory = $true)][string]$Vhdx,
  [int]$MemoryMiB = 4096,
  [int]$Cpus = 2,
  [ValidateSet('plain', 'pager')][string]$Mode = 'plain',
  [ValidateSet('uefi', 'bios')][string]$Firmware = 'uefi',
  [ValidateSet('xpress', 'lz4')][string]$Codec = 'xpress',
  [int]$Cluster = 16,
  [int]$IdleMs = 2000,
  [int]$StartDelayS = 90,
  [switch]$Ghost,
  [int]$RdpPort = 13389,
  [string]$Cpu = 'host',
  [ValidateSet('on', 'off')][string]$Irqchip = 'on',
  [switch]$Headless,
  [switch]$ResetOverlay
)
. (Join-Path $PSScriptRoot 'scripts\Common.ps1')

if (-not (Test-Path $Qemu)) { throw "Не найден $Qemu" }
$Vhdx = (Resolve-Path $Vhdx).Path
if (Get-GuestProcess) { throw 'QEMU-гость уже запущен (см. qemu.pid). Сначала завершите его.' }

# Диск не должен использоваться работающей ВМ Hyper-V.
$busy = $null
try {
  Import-Module Hyper-V -ErrorAction Stop
  $busy = Get-VM | Where-Object { $_.State -ne 'Off' -and (Get-VMHardDiskDrive -VM $_ | Where-Object { $_.Path -eq $Vhdx }) }
} catch { }
if ($busy) { throw "ВМ Hyper-V '$($busy.Name)' запущена с этим диском. Выключите её (Stop-VM) перед запуском в QEMU." }

$overlay = Join-Path $Work 'overlay.qcow2'
if ($ResetOverlay -and (Test-Path $overlay)) { Remove-Item $overlay }
if (-not (Test-Path $overlay)) {
  & $QemuImg create -f qcow2 -F vhdx -b $Vhdx $overlay | Out-Host
  if ($LASTEXITCODE -ne 0) { throw 'qemu-img не смог создать overlay' }
}

# Прокси WHP лежит рядом с QEMU только в режиме pager: QEMU грузит WinHvPlatform.dll из своей папки.
$proxyInQemu = Join-Path (Split-Path $Qemu) 'WinHvPlatform.dll'
if ($Mode -eq 'pager') { Copy-Item $Proxy $proxyInQemu -Force } elseif (Test-Path $proxyInQemu) { Remove-Item $proxyInQemu -Force }

$fwd = "user,id=n0,hostfwd=tcp:127.0.0.1:" + $RdpPort + "-:3389"
$qa = @('-name', 'ElasticMem-guest', '-accel', "whpx,kernel-irqchip=$Irqchip", '-cpu', $Cpu, '-smp', "$Cpus", '-m', "$MemoryMiB",
  '-rtc', 'base=localtime', '-usb', '-device', 'usb-tablet',
  '-drive', "file=$overlay,if=none,id=d0,cache=writeback", '-device', 'ahci,id=ahci', '-device', 'ide-hd,drive=d0,bus=ahci.0',
  '-netdev', $fwd, '-device', 'e1000e,netdev=n0')
if ($Firmware -eq 'uefi') {
  $code = Join-Path $Kit 'qemu\share\edk2-x86_64-code.fd'
  $vars = Join-Path $Work 'vars.fd'
  if (-not (Test-Path $vars)) { Copy-Item (Join-Path $Kit 'qemu\share\edk2-i386-vars.fd') $vars }
  $qa = @('-machine', 'q35') + $qa + @('-drive', "if=pflash,format=raw,readonly=on,file=$code", '-drive', "if=pflash,format=raw,file=$vars")
} else {
  $qa = @('-machine', 'q35') + $qa
}
if ($Headless) { $qa += @('-display', 'none') } else { $qa += @('-display', 'sdl', '-vga', 'std') }
$qa = $qa | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }

$log = Join-Path $Work 'em-pager.log'
if ($Mode -eq 'pager') {
  $env:EM_PAGER = '1'
  $env:EM_PAGER_LOG = $log
  $env:EM_PAGER_CODEC = $Codec
  $env:EM_PAGER_CLUSTER = "$Cluster"
  $env:EM_PAGER_IDLE_MS = "$IdleMs"
  $env:EM_PAGER_START_DELAY_S = "$StartDelayS"
  $env:EM_PAGER_GHOST = if ($Ghost) { '1' } else { '0' }
} else {
  Remove-Item Env:EM_PAGER -ErrorAction SilentlyContinue
}
"$(Get-Date -Format s) start mode=$Mode mem=$MemoryMiB cpus=$Cpus fw=$Firmware codec=$Codec cluster=$Cluster ghost=$Ghost" | Add-Content (Join-Path $Work 'runs.txt')
$sp = @{ FilePath = $Qemu; ArgumentList = $qa; WorkingDirectory = $Work; PassThru = $true;
  RedirectStandardOutput = (Join-Path $Work 'qemu-out.txt'); RedirectStandardError = (Join-Path $Work 'qemu-err.txt') }
$p = Start-Process @sp
$p.Id | Set-Content (Join-Path $Work 'qemu.pid')
Start-Sleep -Seconds 5
if ($p.HasExited) {
  Get-Content (Join-Path $Work 'qemu-err.txt') | Out-Host
  throw "QEMU завершился сразу (код $($p.ExitCode)). Частые причины: не включён компонент «Платформа низкоуровневой оболочки Windows» (HypervisorPlatform); попробуйте -Cpu max; при «Failed to enable nested virtualization» — -Irqchip off; для ВМ поколения 1 — -Firmware bios. Если падает только в режиме pager — пришлите em-pager.log и qemu-err.txt."
}
Write-Host "QEMU запущен (PID $($p.Id)), режим $Mode. RDP гостя: 127.0.0.1:$RdpPort"
Write-Host "Первая загрузка на новом «железе» может идти дольше обычного. Окно приложения: Open-App.ps1"
if ($Mode -eq 'pager') { Write-Host "Журнал пейджера: $log (компактор включится через $StartDelayS с)" }
