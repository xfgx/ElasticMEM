# Пользовательский тест на Windows 10/11 с включённым Memory Compression.
# 1 мин — обычная работа (замер), 5 мин — постраничное вытеснение холодных страниц выбранного приложения.
# Пользуйтесь приложением как обычно всё это время. Отчёт — на рабочий стол.
param([string]$Exe = '', [int]$BaseSec = 60, [int]$ActSec = 300)
$ErrorActionPreference = 'Continue'
$probe = Join-Path $PSScriptRoot 'probe.exe'
$report = Join-Path ([Environment]::GetFolderPath('Desktop')) ("ElasticMem-active-test-{0:yyyyMMdd-HHmm}.txt" -f (Get-Date))
if (-not $Exe) {
  $cands = 'msedge', 'chrome', 'Telegram', 'browser', 'firefox', 'Discord', 'Code', 'steamwebhelper'
  $best = $cands | ForEach-Object { $n = $_; $ps = Get-Process -Name $n -ErrorAction SilentlyContinue; if ($ps) { [pscustomobject]@{ Name = $n; MiB = [int](($ps | Measure-Object PrivateMemorySize64 -Sum).Sum / 1MB) } } } | Sort-Object MiB -Descending | Select-Object -First 1
  if (-not $best) { Write-Host 'Не найдено запущенных браузеров/мессенджеров. Запустите приложение или укажите: ТЕСТ-АКТИВНОЕ-СЖАТИЕ.cmd -Exe имя'; exit 1 }
  $Exe = $best.Name; Write-Host "Приложение: $Exe ($($best.MiB) МиБ приватной памяти)"
}
$exeName = if ($Exe -like '*.exe') { $Exe } else { "$Exe.exe" }
$os = Get-CimInstance Win32_OperatingSystem
$mc = Get-Process -Name 'Memory Compression' -ErrorAction SilentlyContinue
"ElasticMem active-trim user test $(Get-Date -Format s)`nOS: $($os.Caption) $($os.Version), RAM $([int]($os.TotalVisibleMemorySize/1MB)) ГиБ`nMemory Compression process: $(if ($mc) { 'есть' } else { 'НЕТ (сжатия не будет, страницы уйдут в файл подкачки)' })`nApp: $exeName" | Tee-Object $report
Write-Host ''
Write-Host "Первую минуту ничего не меняется (замер). Затем 5 минут агент вытесняет холодные страницы $exeName."
Write-Host 'Пользуйтесь приложением как обычно и обратите внимание, есть ли подтормаживания.'
& $probe realapp $exeName $BaseSec $ActSec 1 2>&1 | Tee-Object -Append $report
Write-Host ''
Write-Host "Отчёт: $report"
$ans = Read-Host 'Были ли заметные подтормаживания? (нет / немного / да) '
"USER_FEEL: $ans" | Add-Content $report
