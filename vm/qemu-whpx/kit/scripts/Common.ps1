# Общие функции набора ElasticMem QEMU/WHPX.
$ErrorActionPreference = 'Stop'
$Kit = Split-Path -Parent $PSScriptRoot
$Qemu = Join-Path $Kit 'qemu\qemu-system-x86_64.exe'
$QemuImg = Join-Path $Kit 'qemu\qemu-img.exe'
$Proxy = Join-Path $Kit 'WinHvPlatform.dll'
$Work = Join-Path $env:LOCALAPPDATA 'ElasticMem\qemu-guest'
New-Item -ItemType Directory -Force $Work | Out-Null

function Get-GuestProcess {
  $pidFile = Join-Path $Work 'qemu.pid'
  if (Test-Path $pidFile) {
    $p = Get-Process -Id ([int](Get-Content $pidFile)) -ErrorAction SilentlyContinue
    if ($p -and $p.ProcessName -like 'qemu-system*') { return $p }
  }
  return Get-Process -Name 'qemu-system-x86_64' -ErrorAction SilentlyContinue | Select-Object -First 1
}

# Одна команда QMP (монитор QEMU, Start-Guest -QmpPort). Возвращает ответ ({return} или {error}) или $null.
function Invoke-Qmp([string]$Execute, [hashtable]$Arguments, [int]$Port = 13390) {
  $c = New-Object Net.Sockets.TcpClient
  try {
    $c.ReceiveTimeout = 5000; $c.SendTimeout = 5000
    $c.Connect('127.0.0.1', $Port)
    $s = $c.GetStream()
    $r = New-Object IO.StreamReader($s, (New-Object Text.UTF8Encoding($false)))
    $w = New-Object IO.StreamWriter($s, (New-Object Text.UTF8Encoding($false)))
    $w.AutoFlush = $true
    [void]$r.ReadLine()
    $res = $null
    foreach ($m in @(@{ execute = 'qmp_capabilities' }, @{ execute = $Execute })) {
      if ($Arguments -and $m.execute -eq $Execute) { $m.arguments = $Arguments }
      $w.WriteLine(($m | ConvertTo-Json -Compress -Depth 5))
      do { $res = $r.ReadLine() | ConvertFrom-Json } until ($res.PSObject.Properties['return'] -or $res.PSObject.Properties['error'])
    }
    return $res
  } catch { return $null } finally { $c.Close() }
}

function Get-MemSnapshot([string]$tag) {
  $q = Get-GuestProcess
  $mc = Get-Process -Name 'Memory Compression' -ErrorAction SilentlyContinue
  $os = Get-CimInstance Win32_OperatingSystem
  [pscustomobject]@{
    tag = $tag
    qemu_ws_mib = if ($q) { [int]($q.WorkingSet64 / 1MB) } else { $null }
    qemu_private_mib = if ($q) { [int]($q.PrivateMemorySize64 / 1MB) } else { $null }
    memcompression_ws_mib = if ($mc) { [int]($mc.WorkingSet64 / 1MB) } else { $null }
    system_free_mib = [int]($os.FreePhysicalMemory / 1KB)
    system_total_mib = [int]($os.TotalVisibleMemorySize / 1KB)
  }
}

function Write-Report([string]$name, [string[]]$lines) {
  $dir = if ($env:EM_REPORT_DIR) { $env:EM_REPORT_DIR } else { [Environment]::GetFolderPath('Desktop') }
  $path = Join-Path $dir ("ElasticMem-$name-{0:yyyyMMdd-HHmm}.txt" -f (Get-Date))
  $lines | Set-Content -Path $path -Encoding UTF8
  Write-Host ""
  Write-Host "Отчёт: $path"
}
