# Полная проверка одной командой: предварительные проверки -> гость в QEMU с пейджером -> ожидание загрузки ->
# замер пейджера -> Gate 0 по памяти гостя -> один zip-отчёт на рабочем столе. В отчёт попадают только
# счётчики, журналы набора и агрегаты em-pagestat — ни содержимого памяти, ни файлов гостя.
#   -PreflightOnly : только проверить окружение и показать, что будет сделано
#   -Vhdx          : диск гостя (иначе — выбор из ВМ Hyper-V)
#   -Rdp           : ваш .rdp для окна приложения (иначе окно не открывается автоматически)
#   -Minutes       : сколько минут мерить пейджер, пока вы пользуетесь приложением (по умолчанию 10)
param([switch]$PreflightOnly, [string]$Vhdx, [string]$Rdp, [int]$Minutes = 10, [int]$MemoryMiB = 0, [int]$BootTimeoutMin = 15)
. (Join-Path $PSScriptRoot 'scripts\Common.ps1')
$ErrorActionPreference = 'Continue'
$stamp = '{0:yyyyMMdd-HHmm}' -f (Get-Date)
$rep = Join-Path $Work "report-$stamp"
New-Item -ItemType Directory -Force $rep | Out-Null
$env:EM_REPORT_DIR = $rep
$log = Join-Path $rep 'run-all.txt'
function Say([string]$s) { Write-Host $s; Add-Content -Path $log -Value ("{0:HH:mm:ss} {1}" -f (Get-Date), $s) -Encoding UTF8 }
function Finish([string]$why) {
  Say "ИТОГ: $why"
  foreach ($f in 'em-pager.log', 'qemu-err.txt', 'qemu-out.txt', 'runs.txt') { $p = Join-Path $Work $f; if (Test-Path $p) { Copy-Item $p $rep } }
  $desk = [Environment]::GetFolderPath('Desktop')
  if (-not $desk) { $desk = $env:USERPROFILE }
  New-Item -ItemType Directory -Force $desk | Out-Null
  $zip = Join-Path $desk "ElasticMem-report-$stamp.zip"
  Compress-Archive -Path (Join-Path $rep '*') -DestinationPath $zip -Force
  Write-Host ""
  Write-Host "Отчёт для отправки: $zip"
  exit 0
}

# ---------- предварительные проверки ----------
$os = Get-CimInstance Win32_OperatingSystem
Say "ОС: $($os.Caption) $($os.Version), RAM $([int]($os.TotalVisibleMemorySize/1MB)) ГиБ, свободно $([int]($os.FreePhysicalMemory/1MB)) ГиБ"
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Say "Администратор: $admin"
$whp = $null
try { $whp = (Get-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform -ErrorAction Stop).State } catch { $whp = 'unknown (нужны права администратора)' }
Say "Платформа низкоуровневой оболочки Windows (HypervisorPlatform): $whp"
$ok = $true
if (-not (Test-Path $Qemu)) { Say "НЕТ: $Qemu — распакуйте набор целиком"; $ok = $false }
if (-not (Test-Path $Proxy)) { Say "НЕТ: $Proxy"; $ok = $false }
if ("$whp" -eq 'Disabled') { Say 'НУЖНО: включите компонент в optionalfeatures.exe («Платформа низкоуровневой оболочки Windows») и перезагрузитесь'; $ok = $false }

$vm = $null
$fw = 'uefi'
if (-not $Vhdx) {
  try {
    Import-Module Hyper-V -ErrorAction Stop
    $vms = @(Get-VM | ForEach-Object { $v = $_; Get-VMHardDiskDrive -VM $v | Select-Object -First 1 | ForEach-Object { [pscustomobject]@{ Name = $v.Name; State = "$($v.State)"; Gen = $v.Generation; MemMiB = [int]($v.MemoryStartup / 1MB); Disk = $_.Path } } })
    if (-not $vms) { Say 'ВМ Hyper-V с дисками не найдены — укажите -Vhdx'; $ok = $false }
    else {
      for ($i = 0; $i -lt $vms.Count; $i++) { Say ("  [{0}] {1} ({2}, поколение {3}, {4} МиБ): {5}" -f ($i + 1), $vms[$i].Name, $vms[$i].State, $vms[$i].Gen, $vms[$i].MemMiB, $vms[$i].Disk) }
      if (-not $PreflightOnly) {
        $n = if ($vms.Count -eq 1) { 1 } else { [int](Read-Host 'Номер ВМ') }
        $vm = $vms[$n - 1]
        $Vhdx = $vm.Disk
        if ($vm.Gen -eq 1) { $fw = 'bios' }
        $snaps = @(Get-VMSnapshot -VMName $vm.Name -ErrorAction SilentlyContinue)
        if ($snaps.Count -gt 0) { Say "ВНИМАНИЕ: у ВМ есть контрольные точки ($($snaps.Count)). Текущее состояние лежит в разностном диске .avhdx, а QEMU разностные VHDX не читает. Удалите контрольные точки в Hyper-V (их изменения сольются в основной диск) или укажите -Vhdx на нужный диск." }
        if ($MemoryMiB -eq 0) { $MemoryMiB = [Math]::Max(2048, [Math]::Min(8192, $vm.MemMiB)) }
        if ($vm.State -ne 'Off') {
          $a = Read-Host "ВМ '$($vm.Name)' сейчас $($vm.State). Выключить её штатно (Stop-VM)? [y/N]"
          if ($a -match '^[yYдД]') { Stop-VM -Name $vm.Name; Say "ВМ $($vm.Name) выключена" } else { Say 'ВМ не выключена — диск занят'; $ok = $false }
        }
      }
    }
  } catch { Say "Hyper-V недоступен ($($_.Exception.Message)) — укажите -Vhdx"; if (-not $PreflightOnly) { $ok = $false } }
}
if ($MemoryMiB -eq 0) { $MemoryMiB = 4096 }
# Диск должен быть виден из ЭТОГО окна: сетевые диски и диски, подключённые через subst, в окне администратора
# не видны (другой сеанс входа), даже если Hyper-V их показывает.
while ($Vhdx -and -not (Test-Path $Vhdx)) {
  Say "НЕТ: файл диска '$Vhdx' не найден из этого окна."
  Say '  Возможные причины: диск перемещён или удалён; буква диска — сетевой диск или subst (в окне администратора не видна); внешний диск не подключён.'
  if ($PreflightOnly) { $ok = $false; break }
  $Vhdx = (Read-Host 'Введите полный путь к .vhdx (можно UNC \\сервер\папка\диск.vhdx), Enter — отмена').Trim('"', ' ')
  if (-not $Vhdx) { Finish 'путь к диску гостя не указан' }
}
if ($Vhdx -and $Vhdx -match '\.avhdx$') { Say "НЕТ: '$Vhdx' — разностный диск контрольной точки. QEMU его не читает: удалите контрольные точки ВМ в Hyper-V и укажите основной .vhdx"; $ok = $false }
Say "Диск: $Vhdx; прошивка: $fw; память гостя: $MemoryMiB МиБ"
if ($PreflightOnly) { Finish $(if ($ok) { 'предварительные проверки пройдены' } else { 'есть что исправить (см. строки НЕТ/НУЖНО)' }) }
if (-not $ok) { Finish 'предварительные проверки не пройдены' }
Write-Host ''
Say 'Диску гостя нужна подготовка для QEMU: загрузочный драйвер AHCI (иначе синий экран INACCESSIBLE_BOOT_DEVICE) и разрешённый RDP.'
$a = Read-Host 'Подготовить диск автоматически с хоста, без загрузки гостя (рекомендуется; делается резервная копия куста SYSTEM)? [Y/n]'
if ($a -notmatch '^[nNнН]') {
  try {
    & (Join-Path $PSScriptRoot 'Prepare-Disk.ps1') -Vhdx $Vhdx 2>&1 | ForEach-Object { Say "$_" }
  } catch { Say "Prepare-Disk: $($_.Exception.Message)"; Finish 'не удалось подготовить диск автоматически — запустите Prepare-Guest.ps1 внутри гостя в Hyper-V и повторите' }
} else {
  $a = Read-Host 'Тогда Prepare-Guest.ps1 уже запускался внутри гостя? [y/N]'
  if ($a -notmatch '^[yYдД]') { Finish 'подготовьте диск: пункт 0 с автоматической подготовкой, либо Prepare-Guest.ps1 внутри гостя в Hyper-V' }
}

# ---------- запуск ----------
$irq = 'on'
$started = $false
for ($try = 1; $try -le 2 -and -not $started; $try++) {
  try {
    & (Join-Path $PSScriptRoot 'Start-Guest.ps1') -Vhdx $Vhdx -Mode pager -Firmware $fw -MemoryMiB $MemoryMiB -Irqchip $irq 2>&1 | ForEach-Object { Say "$_" }
    $started = [bool](Get-GuestProcess)
  } catch { Say "Start-Guest: $($_.Exception.Message)" }
  if (-not $started) {
    $err = Get-Content (Join-Path $Work 'qemu-err.txt') -Raw -ErrorAction SilentlyContinue
    if ($irq -eq 'on' -and "$err" -match 'nested virtualization|kernel-irqchip|irqchip') { Say 'Повтор с -Irqchip off'; $irq = 'off' } else { break }
  }
}
if (-not $started) { Finish 'QEMU не запустился (см. qemu-err.txt в отчёте)' }

# ---------- ожидание загрузки гостя ----------
Say "Жду загрузки гостя (RDP на 127.0.0.1:13389, до $BootTimeoutMin мин). В окне QEMU виден ход загрузки."
$t0 = Get-Date
$booted = $false
while (((Get-Date) - $t0).TotalMinutes -lt $BootTimeoutMin) {
  if (-not (Get-GuestProcess)) { break }
  try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', 13389); $c.Close(); $booted = $true; break } catch { }
  Start-Sleep -Seconds 10
}
Say ("Загрузка: {0} за {1:N0} с" -f $(if ($booted) { 'гость отвечает по RDP' } else { 'не дождался' }), ((Get-Date) - $t0).TotalSeconds)
if (-not (Get-GuestProcess)) { Finish 'QEMU завершился во время загрузки (если на экране был синий экран INACCESSIBLE_BOOT_DEVICE — не запускали Prepare-Guest.ps1)' }
if (-not $booted) { Say 'RDP не ответил: замеряю всё равно (возможно, в госте выключен RDP)' }

# ---------- замеры ----------
if ($Rdp) { & (Join-Path $PSScriptRoot 'Open-App.ps1') -Rdp $Rdp }
Say "Замер пейджера $Minutes мин. Пользуйтесь приложением гостя как обычно (окно — пункт 3 меню или ваш .rdp)."
& (Join-Path $PSScriptRoot 'Measure.ps1') -Mode track2 -Seconds ($Minutes * 60) 2>&1 | ForEach-Object { Add-Content -Path $log -Value "$_" -Encoding UTF8 }
Say 'Gate 0 по памяти гостя (два снимка с паузой 60 с)'
& (Join-Path $PSScriptRoot 'Measure.ps1') -Mode pagestat 2>&1 | ForEach-Object { Add-Content -Path $log -Value "$_" -Encoding UTF8 }
$last = Get-Content (Join-Path $Work 'em-pager.log') -Tail 1 -ErrorAction SilentlyContinue
Say "Последняя строка пейджера: $last"
Write-Host ''
$a = Read-Host 'Завершить гостя? Выключите Windows в госте сами (Пуск -> Завершение работы) и нажмите Enter, или введите k, чтобы завершить QEMU принудительно'
if ($a -eq 'k') { $q = Get-GuestProcess; if ($q) { Stop-Process -Id $q.Id -Force } }
Finish 'проверка завершена'
