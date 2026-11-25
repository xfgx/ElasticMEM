# Замеры набора. Все цифры — со счётчиков Windows (рабочий набор/приватная память qemu, процесс Memory Compression).
#   -Mode track1   : агент probe.exe постранично вытесняет холодную память процесса qemu (сжимает Windows)
#   -Mode track2   : читает журнал em-pager (своё хранилище) и счётчики процесса
#   -Mode pagestat : em-pagestat по памяти гостя (сколько дали бы zero / dedup / delta / временна́я дельта)
#   -Mode host     : em-pagestat по вашим приложениям на хосте (браузеры, мессенджеры) — Gate 0 без ВМ
param([ValidateSet('track1', 'track2', 'pagestat', 'host')][string]$Mode = 'track2', [int]$Seconds = 300)
. (Join-Path $PSScriptRoot 'scripts\Common.ps1')
$out = New-Object System.Collections.Generic.List[string]
$os = Get-CimInstance Win32_OperatingSystem
$out.Add("ElasticMem $Mode $(Get-Date -Format s) | $($os.Caption) $($os.Version) | RAM $([int]($os.TotalVisibleMemorySize/1MB)) ГиБ")
$out.Add("runs: " + ((Get-Content (Join-Path $Work 'runs.txt') -ErrorAction SilentlyContinue | Select-Object -Last 1)))
$pagestat = Join-Path $Kit 'em-pagestat.exe'
switch ($Mode) {
  'track1' {
    $q = Get-GuestProcess; if (-not $q) { throw 'QEMU-гость не запущен' }
    $out.Add("BEFORE " + ((Get-MemSnapshot 'before') | ConvertTo-Json -Compress))
    Write-Host "1 мин замер, затем $Seconds с постраничное вытеснение холодной памяти qemu. Пользуйтесь приложением как обычно."
    & (Join-Path $Kit 'probe.exe') realapp 'qemu-system-x86_64.exe' 60 $Seconds 1 2>&1 | ForEach-Object { $out.Add("$_"); $_ } | Out-Host
    $out.Add("AFTER " + ((Get-MemSnapshot 'after') | ConvertTo-Json -Compress))
  }
  'track2' {
    $log = Join-Path $Work 'em-pager.log'
    $out.Add("START " + ((Get-MemSnapshot 'start') | ConvertTo-Json -Compress))
    Write-Host "Наблюдаю $Seconds с. Пользуйтесь приложением как обычно."
    $t = 0
    while ($t -lt $Seconds) { Start-Sleep 30; $t += 30; $l = Get-Content $log -Tail 1 -ErrorAction SilentlyContinue; Write-Host $l; $out.Add("$l"); $out.Add("SNAP " + ((Get-MemSnapshot "t$t") | ConvertTo-Json -Compress)) }
    $out.Add('--- журнал пейджера (начало) ---'); Get-Content $log -TotalCount 12 -ErrorAction SilentlyContinue | ForEach-Object { $out.Add($_) }
  }
  'pagestat' {
    $q = Get-GuestProcess; if (-not $q) { throw 'QEMU-гость не запущен' }
    Write-Host 'Читаю резидентную память гостя (только расчёт в памяти, наружу — только агрегаты). Два снимка с паузой 60 с.'
    & $pagestat --pid $q.Id --private-only --snapshots 2 --interval-ms 60000 --max-mib 32768 2>&1 | ForEach-Object { $out.Add("$_"); $_ } | Out-Host
  }
  'host' {
    $names = 'msedge', 'chrome', 'firefox', 'Telegram', 'Discord', 'Code', 'steamwebhelper', 'browser'
    $a = @(); foreach ($n in $names) { if (Get-Process -Name $n -ErrorAction SilentlyContinue) { $a += @('--name', $n); $out.Add("app: $n") } }
    if (-not $a) { throw 'Не найдено запущенных браузеров/мессенджеров' }
    & $pagestat @a --snapshots 2 --interval-ms 60000 --max-mib 8192 2>&1 | ForEach-Object { $out.Add("$_"); $_ } | Out-Host
  }
}
Write-Report $Mode $out
