# Полная проверка одной командой: предварительные проверки -> гость в QEMU с пейджером -> ожидание загрузки ->
# замер пейджера -> Gate 0 по памяти гостя -> один zip-отчёт на рабочем столе. В отчёт попадают только
# счётчики, журналы набора и агрегаты em-pagestat — ни содержимого памяти, ни файлов гостя.
#   -PreflightOnly : только проверить окружение и показать, что будет сделано
#   -Vhdx          : диск гостя (иначе — выбор из ВМ Hyper-V)
#   -Rdp           : ваш .rdp для окна приложения (иначе окно не открывается автоматически)
#   -Minutes       : сколько минут мерить пейджер, пока вы пользуетесь приложением (по умолчанию 10)
#   -Mode          : pager (по умолчанию) или plain — тот же гость без пейджера, для сравнения, если гость падает
param([switch]$PreflightOnly, [string]$Vhdx, [string]$Rdp, [int]$Minutes = 10, [int]$MemoryMiB = 0, [int]$BootTimeoutMin = 15, [ValidateSet('pager', 'plain')][string]$Mode = 'pager')
. (Join-Path $PSScriptRoot 'scripts\Common.ps1')
$ErrorActionPreference = 'Continue'
$runStart = Get-Date
$stamp = '{0:yyyyMMdd-HHmm}' -f (Get-Date)
$rep = Join-Path $Work "report-$stamp"
New-Item -ItemType Directory -Force $rep | Out-Null
$env:EM_REPORT_DIR = $rep
$log = Join-Path $rep 'run-all.txt'
function Say([string]$s) { Write-Host $s; Add-Content -Path $log -Value ("{0:HH:mm:ss} {1}" -f (Get-Date), $s) -Encoding UTF8 }
# Настоящая проверка RDP: QEMU (slirp) принимает TCP-соединение на проброшенном порту ещё до загрузки гостя,
# поэтому ждём ответ сервера RDP на запрос X.224 Connection Request.
function Test-Rdp([int]$port = 13389) {
  try {
    $c = New-Object Net.Sockets.TcpClient
    $c.ReceiveTimeout = 3000
    $c.Connect('127.0.0.1', $port)
    $s = $c.GetStream()
    $req = [byte[]](3, 0, 0, 19, 14, 224, 0, 0, 0, 0, 0, 1, 0, 8, 0, 3, 0, 0, 0)
    $s.Write($req, 0, $req.Length)
    $buf = New-Object byte[] 64
    $n = $s.Read($buf, 0, 64)
    $c.Close()
    return ($n -ge 4 -and $buf[0] -eq 3)
  } catch { return $false }
}
function Finish([string]$why) {
  $qe = (Get-Content (Join-Path $Work 'qemu-err.txt') -Raw -ErrorAction SilentlyContinue) + (Get-Content (Join-Path $Work 'qemu-out.txt') -Raw -ErrorAction SilentlyContinue)
  if ("$qe" -match 'Unexpected VP exit code 4') { Say 'ДИАГНОЗ: гость аварийно остановился (тройной сбой после синего экрана). Сравните с запуском без пейджера: Run-All.ps1 -Mode plain' }
  if ("$qe" -match 'Unimplemented handler') { Say 'ДИАГНОЗ: QEMU получил обращение гостя к памяти как к устройству (Unimplemented handler) — признак ошибки пейджера' }
  $pl = Get-Content (Join-Path $Work 'em-pager.log') -Raw -ErrorAction SilentlyContinue
  if ("$pl" -match 'FATAL') { Say 'ДИАГНОЗ: пейджер остановил QEMU (FATAL в em-pager.log), чтобы не испортить память гостя' }
  foreach ($ln in @(("$pl" -split "`n") | Where-Object { $_ -match '^EM_PAGER (CRASH|PANIC)' } | Select-Object -First 3)) { Say "ДИАГНОЗ: $($ln.Trim())" }
  # Падение процесса QEMU по журналу Windows (событие 1000 «Application Error»): код исключения, модуль, смещение
  try {
    $ev = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000; StartTime = $runStart } -ErrorAction Stop |
      Where-Object { "$($_.Properties[0].Value)" -like 'qemu-system-x86_64*' } | Select-Object -First 3)
    if ($ev.Count) {
      $ev | ForEach-Object { "$($_.TimeCreated) $($_.Message)" } | Set-Content (Join-Path $rep 'crash-events.txt') -Encoding UTF8
      $e0 = $ev[0].Properties
      Say "ДИАГНОЗ: Windows зафиксировал падение QEMU: исключение $($e0[6].Value), модуль $($e0[3].Value), смещение $($e0[7].Value)"
    }
  } catch {}
  $tl = @(([regex]'EM_PAGER t_s=(\d+) .*? evicted_mib=([\d.]+) .*? evictions=(\d+)').Matches("$pl"))
  if ($tl.Count) {
    $lt = $tl[$tl.Count - 1]
    if ([int]$lt.Groups[3].Value -eq 0) { Say "ДИАГНОЗ: к $($lt.Groups[1].Value)-й секунде пейджер ещё ничего не вытеснил — сбой не из-за вытеснения памяти" }
    else { Say "Пейджер к $($lt.Groups[1].Value)-й секунде: вытеснено $($lt.Groups[2].Value) МиБ, вытеснений $($lt.Groups[3].Value)" }
  }
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
        $n = 1
        if ($vms.Count -gt 1) {
          do { $s = Read-Host "Номер ВМ (1-$($vms.Count))" } until ([int]::TryParse($s.Trim(), [ref]$n) -and $n -ge 1 -and $n -le $vms.Count)
        }
        $vm = $vms[$n - 1]
        $Vhdx = $vm.Disk
        if ($vm.Gen -eq 1) { $fw = 'bios' }
        $snaps = @(Get-VMSnapshot -VMName $vm.Name -ErrorAction SilentlyContinue)
        if ($snaps.Count -gt 0) { Say "У ВМ есть контрольные точки ($($snaps.Count)); для QEMU будет собрана отдельная полная копия диска." }
        if ($v = Get-VM -Name $vm.Name -ErrorAction SilentlyContinue) { if ($v.AutomaticCheckpointsEnabled) { Say 'Подсказка: у ВМ включены автоматические контрольные точки — каждый запуск в Hyper-V создаёт новый .avhdx (Hyper-V: Параметры ВМ -> Контрольные точки).' } }
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
# Разностный диск (контрольная точка, .avhdx): QEMU его не читает. Собираем из цепочки отдельную полную копию
# штатным Convert-VHD — диски ВМ и её контрольные точки при этом не меняются.
$vhdInfo = $null
if ($Vhdx -and (Test-Path $Vhdx)) { try { Import-Module Hyper-V -ErrorAction Stop; $vhdInfo = Get-VHD -Path $Vhdx -ErrorAction Stop } catch { } }
$isDiff = ($vhdInfo -and "$($vhdInfo.VhdType)" -eq 'Differencing') -or ($Vhdx -match '\.avhdx$')
if ($isDiff) {
  Say "Диск '$Vhdx' — разностный (контрольная точка). QEMU читает только полный диск."
  $chainOk = $true
  $cur = $vhdInfo
  $used = 0
  while ($cur) {
    $used += $cur.FileSize
    Say ("  цепочка: {0} ({1}, {2:N1} ГиБ)" -f $cur.Path, $cur.VhdType, ($cur.FileSize / 1GB))
    if ("$($cur.VhdType)" -ne 'Differencing') { break }
    if (-not $cur.ParentPath -or -not (Test-Path $cur.ParentPath)) { Say "НЕТ: родительский диск '$($cur.ParentPath)' не найден — цепочка контрольных точек разорвана"; $chainOk = $false; break }
    try { $cur = Get-VHD -Path $cur.ParentPath -ErrorAction Stop } catch { Say "НЕТ: не читается '$($cur.ParentPath)': $($_.Exception.Message)"; $chainOk = $false; break }
  }
  if (-not $vhdInfo) { Say 'НЕТ: без модуля Hyper-V цепочку не собрать — удалите контрольные точки ВМ в Hyper-V и укажите основной .vhdx'; $chainOk = $false }
  if (-not $chainOk) { $ok = $false }
  elseif (-not $PreflightOnly) {
    $flat = Join-Path (Split-Path $Vhdx) 'ElasticMem-qemu-copy.vhdx'
    $drive = (Get-Item (Split-Path $Vhdx)).PSDrive
    $freeGiB = if ($drive.Free) { [int]($drive.Free / 1GB) } else { -1 }
    $needGiB = [int][Math]::Ceiling($used / 1GB)
    if ((Test-Path $flat) -and (Get-Item $flat).LastWriteTime -gt (Get-Item $Vhdx).LastWriteTime) {
      $q = Read-Host "Есть копия для QEMU новее контрольной точки: $flat. Использовать её? [Y/n]"
      if ($q -notmatch '^[nNнН]') { $Vhdx = $flat } else { Remove-Item $flat -Force }
    }
    if ($Vhdx -ne $flat) {
      Say "Нужно до ~$needGiB ГиБ свободного места на $($drive.Name):, свободно $freeGiB ГиБ."
      $q = Read-Host "Собрать полную копию диска для QEMU в $flat (оригинал ВМ не меняется, займёт несколько минут)? [Y/n]"
      if ($q -match '^[nNнН]') { Finish 'нужна полная копия диска или ВМ без контрольных точек' }
      if ($freeGiB -ge 0 -and $freeGiB -lt $needGiB) { Finish "мало места на $($drive.Name): для копии (нужно ~$needGiB ГиБ)" }
      $t0 = Get-Date
      try { Convert-VHD -Path $Vhdx -DestinationPath $flat -VHDType Dynamic -ErrorAction Stop } catch { Say "Convert-VHD: $($_.Exception.Message)"; Finish 'не удалось собрать копию диска' }
      Say ("Копия собрана за {0:N0} с: {1}" -f ((Get-Date) - $t0).TotalSeconds, $flat)
      $Vhdx = $flat
    }
  }
}
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

# ---------- самопроверка пейджера на настоящем гипервизоре (без Windows-гостя) ----------
$st = Join-Path $PSScriptRoot 'whp-selftest.exe'
if ($Mode -eq 'pager' -and (Test-Path $st)) {
  Say 'Самопроверка пейджера на этом гипервизоре: маленькая ВМ на 60 с, без Windows-гостя.'
  $env:EM_PAGER = '1'; $env:EM_PAGER_START_DELAY_S = '0'; $env:EM_PAGER_IDLE_MS = '300'; $env:EM_PAGER_LOG = (Join-Path $rep 'selftest-pager.log')
  Push-Location $PSScriptRoot
  $out = & $st 512 60 2 2>&1
  Pop-Location
  Remove-Item Env:EM_PAGER, Env:EM_PAGER_START_DELAY_S, Env:EM_PAGER_IDLE_MS, Env:EM_PAGER_LOG -ErrorAction SilentlyContinue
  $out | ForEach-Object { Say "$_" }
  $v = "$out"
  if ($v -match 'verdict=FAIL') { Finish 'самопроверка пейджера на гипервизоре НЕ прошла — гостя с пейджером не запускаю (отчёт с журналом самопроверки)' }
  elseif ($v -match 'verdict=PASS') { Say 'Самопроверка пройдена.' }
  else { Say 'Самопроверка не выполнилась (см. строку выше) — продолжаю.' }
}

# ---------- запуск ----------
$irq = 'on'
$started = $false
for ($try = 1; $try -le 2 -and -not $started; $try++) {
  try {
    & (Join-Path $PSScriptRoot 'Start-Guest.ps1') -Vhdx $Vhdx -ResetOverlay -Mode $Mode -Firmware $fw -MemoryMiB $MemoryMiB -Irqchip $irq 2>&1 | ForEach-Object { Say "$_" }
    $started = [bool](Get-GuestProcess)
  } catch { Say "Start-Guest: $($_.Exception.Message)" }
  if (-not $started) {
    $err = Get-Content (Join-Path $Work 'qemu-err.txt') -Raw -ErrorAction SilentlyContinue
    if ($irq -eq 'on' -and "$err" -match 'nested virtualization|kernel-irqchip|irqchip') { Say 'Повтор с -Irqchip off'; $irq = 'off' } else { break }
  }
}
if (-not $started) { Finish 'QEMU не запустился (см. qemu-err.txt в отчёте)' }

# QEMU не завершается, когда останавливает ВМ (в заголовке окна [Stopped]): статус берём по QMP. При остановке —
# снимок экрана гостя (на синем экране виден код ошибки) в отчёт, QEMU закрывается, отчёт собирается сразу.
function Test-GuestStopped([string]$when) {
  $q = Invoke-Qmp 'query-status'
  $st = if ($q -and $q.PSObject.Properties['return']) { "$($q.return.status)" } else { '' }
  if ($st -notin 'internal-error', 'io-error', 'paused', 'shutdown', 'guest-panicked', 'watchdog') { return }
  $png = Join-Path $rep "screen-$st.png"
  [void](Invoke-Qmp 'screendump' @{ filename = $png; format = 'png' })
  Say "QEMU остановил ВМ $when (статус $st). Снимок экрана гостя: $(Split-Path $png -Leaf)"
  if ($st -eq 'io-error') { Say 'ДИАГНОЗ: ошибка ввода-вывода диска гостя (io-error) — QEMU приостановил ВМ' }
  if ($st -eq 'guest-panicked') { Say 'ДИАГНОЗ: гость сообщил о панике / процессор гостя в неустранимом состоянии (guest-panicked)' }
  [void](Invoke-Qmp 'quit')
  Start-Sleep -Seconds 2
  Finish "ВМ остановлена $when (статус $st)"
}

# ---------- ожидание загрузки гостя ----------
Say "Жду загрузки гостя (RDP на 127.0.0.1:13389, до $BootTimeoutMin мин). В окне QEMU виден ход загрузки."
$t0 = Get-Date
$booted = $false
while (((Get-Date) - $t0).TotalMinutes -lt $BootTimeoutMin) {
  if (-not (Get-GuestProcess)) { break }
  Test-GuestStopped 'во время загрузки'
  if (Test-Rdp) { $booted = $true; break }
  Start-Sleep -Seconds 10
}
Say ("Загрузка: {0} за {1:N0} с" -f $(if ($booted) { 'гость отвечает по RDP' } else { 'не дождался' }), ((Get-Date) - $t0).TotalSeconds)
if (-not (Get-GuestProcess)) { Finish 'QEMU завершился во время загрузки (если на экране был синий экран INACCESSIBLE_BOOT_DEVICE — не запускали Prepare-Guest.ps1)' }
if (-not $booted) { Say 'RDP не ответил: замеряю всё равно. В гость можно войти прямо в окне QEMU.' }

# ---------- окно гостя ----------
if ($Rdp) { & (Join-Path $PSScriptRoot 'Open-App.ps1') -Rdp $Rdp }
elseif ($booted) {
  Start-Process mstsc.exe -ArgumentList '/v:127.0.0.1:13389'
  Say 'Открыл удалённый рабочий стол гостя (mstsc). Войдите учётной записью администратора гостя.'
}
Say 'Войти в гостя можно и прямо в окне QEMU. Запустите там приложения, которыми пользуетесь (браузер и т. п.).'
# (переменные PowerShell не различают регистр: $mode — это тот же $Mode с ValidateSet, поэтому другое имя)
$what = if ($Mode -eq 'pager') { 'пейджера' } else { 'гостя без пейджера' }
Say "Замер $what $Minutes мин. Пользуйтесь приложениями в госте как обычно."
$mm = if ($Mode -eq 'pager') { 'track2' } else { 'idle' }
if ($mm -eq 'track2') {
  & (Join-Path $PSScriptRoot 'Measure.ps1') -Mode track2 -Seconds ($Minutes * 60) 2>&1 | ForEach-Object { Add-Content -Path $log -Value "$_" -Encoding UTF8 }
} else {
  for ($t = 0; $t -lt $Minutes * 60; $t += 30) {
    Start-Sleep 30
    $snap = (Get-MemSnapshot "t$t") | ConvertTo-Json -Compress
    Add-Content -Path $log -Value "SNAP $snap" -Encoding UTF8
    if (-not (Get-GuestProcess)) { Say 'QEMU завершился во время замера'; break }
    Test-GuestStopped 'во время замера'
  }
}
if (-not (Get-GuestProcess)) { Finish 'QEMU завершился во время замера' }
Test-GuestStopped 'во время замера'
Say 'Gate 0 по памяти гостя (два снимка с паузой 60 с)'
& (Join-Path $PSScriptRoot 'Measure.ps1') -Mode pagestat 2>&1 | ForEach-Object { Add-Content -Path $log -Value "$_" -Encoding UTF8 }
$last = Get-Content (Join-Path $Work 'em-pager.log') -Tail 1 -ErrorAction SilentlyContinue
Say "Последняя строка пейджера: $last"
Write-Host ''
$a = Read-Host 'Завершить гостя? Выключите Windows в госте сами (Пуск -> Завершение работы) и нажмите Enter, или введите k, чтобы завершить QEMU принудительно'
if ($a -eq 'k') { $q = Get-GuestProcess; if ($q) { Stop-Process -Id $q.Id -Force } }
Finish 'проверка завершена'
