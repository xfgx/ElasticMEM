# Запускает настоящий браузер (Edge/Chrome headless) с «активной» страницей и несколькими реальными сайтами,
# прогоняет probe.exe realapp (baseline, затем постраничное вытеснение) и читает время кадров через CDP.
param([string]$Probe = '.\probe.exe', [int]$BaseSec = 40, [int]$ActSec = 120, [int]$Method = 1, [string]$Out = 'realapp.txt')
$ErrorActionPreference = 'Continue'
$cands = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Google\Chrome\Application\chrome.exe")
$browser = $cands | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $browser) { "REAL no browser found" | Tee-Object $Out; exit 0 }
$exe = Split-Path $browser -Leaf
"REAL browser=$browser" | Tee-Object $Out
$page = 'file:///' + ((Resolve-Path "$PSScriptRoot\active.html").Path -replace '\\', '/')
$sites = @($page, 'https://en.wikipedia.org/wiki/Microsoft_Windows', 'https://github.com/microsoft/terminal', 'https://www.youtube.com/', 'https://developer.mozilla.org/en-US/docs/Web/JavaScript')
$i = 0
foreach ($u in $sites) {
  $dir = Join-Path $env:RUNNER_TEMP "prof$i"
  $bargs = @('--headless=new', "--user-data-dir=$dir", '--no-first-run', '--no-default-browser-check', '--autoplay-policy=no-user-gesture-required', '--window-size=1280,720')
  if ($i -eq 0) { $bargs += '--remote-debugging-port=9222' }
  Start-Process -FilePath $browser -ArgumentList ($bargs + $u) | Out-Null
  $i++
}
Start-Sleep -Seconds 45
Get-Process ($exe -replace '\.exe$', '') -ErrorAction SilentlyContinue | Sort-Object WorkingSet64 -Descending | Select-Object -First 12 Id, @{n='WS_MiB';e={[int]($_.WorkingSet64/1MB)}}, @{n='Private_MiB';e={[int]($_.PrivateMemorySize64/1MB)}} | Format-Table | Out-String | Tee-Object -Append $Out

& $Probe realapp $exe $BaseSec $ActSec $Method 2>&1 | Tee-Object -Append $Out

function Invoke-Cdp([string]$expr) {
  $tabs = Invoke-RestMethod 'http://127.0.0.1:9222/json' -TimeoutSec 10
  $tab = $tabs | Where-Object { $_.type -eq 'page' } | Select-Object -First 1
  $ws = New-Object System.Net.WebSockets.ClientWebSocket
  $ws.ConnectAsync([Uri]$tab.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait()
  $msg = @{ id = 1; method = 'Runtime.evaluate'; params = @{ expression = $expr; returnByValue = $true } } | ConvertTo-Json -Compress
  $bytes = [Text.Encoding]::UTF8.GetBytes($msg)
  $ws.SendAsync([ArraySegment[byte]]$bytes, 'Text', $true, [Threading.CancellationToken]::None).Wait()
  $buf = New-Object byte[] 1048576; $sb = New-Object Text.StringBuilder
  do { $r = $ws.ReceiveAsync([ArraySegment[byte]]$buf, [Threading.CancellationToken]::None); $r.Wait(); [void]$sb.Append([Text.Encoding]::UTF8.GetString($buf, 0, $r.Result.Count)) } while (-not $r.Result.EndOfMessage)
  $ws.Dispose()
  return ($sb.ToString() | ConvertFrom-Json).result.result.value
}
$log = Get-Content $Out
foreach ($ph in 'baseline', 'page_precise') {
  $s = ($log | Select-String "REAL PHASE_START $ph unix_ms=(\d+)").Matches[0].Groups[1].Value
  $e = ($log | Select-String "REAL PHASE_END $ph unix_ms=(\d+)").Matches[0].Groups[1].Value
  if ($s -and $e) {
    $from = [int64]$s + ([int64]$e - [int64]$s) / 2   # вторая половина фазы, как и память
    try { "REAL FRAMES $ph " + (Invoke-Cdp "window.__stats($from, $e)") | Tee-Object -Append $Out } catch { "REAL FRAMES $ph cdp_error $_" | Tee-Object -Append $Out }
  }
}
Get-Process ($exe -replace '\.exe$', '') -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
