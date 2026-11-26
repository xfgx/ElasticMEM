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
