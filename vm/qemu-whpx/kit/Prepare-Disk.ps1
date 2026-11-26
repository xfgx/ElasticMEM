# Подготовка диска гостя С ХОСТА, без загрузки гостя в Hyper-V (вместо Prepare-Guest.ps1). Нужны права администратора,
# ВМ должна быть выключена. Делает то же, что Prepare-Guest.ps1, но офлайн, в реестре SYSTEM внутри .vhdx:
#  * загрузочные драйверы AHCI/NVMe (storahci, stornvme: Start=0) — иначе в QEMU INACCESSIBLE_BOOT_DEVICE;
#  * разрешает RDP (fDenyTSConnections=0) и включает входящие правила брандмауэра для порта 3389.
# Перед изменением копирует куст SYSTEM рядом (SYSTEM.elasticmem.bak). Диск после работы отключается.
param([Parameter(Mandatory = $true)][string]$Vhdx)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Запустите от администратора' }
if (-not (Test-Path $Vhdx)) { throw "Нет файла $Vhdx" }
$Vhdx = (Resolve-Path $Vhdx).Path
$hive = 'HKLM\EMGUEST'
$mounted = $false
$loaded = $false
try {
  # Mount-DiskImage (модуль Storage) работает и без модуля Hyper-V
  $d = Mount-DiskImage -ImagePath $Vhdx -StorageType VHDX -PassThru | Get-DiskImage | Get-Disk
  $mounted = $true
  Start-Sleep -Seconds 2
  $win = $null
  foreach ($p in (Get-Partition -DiskNumber $d.Number | Where-Object { $_.Type -ne 'Reserved' -and $_.Size -gt 1GB })) {
    if (-not $p.DriveLetter -or $p.DriveLetter -eq [char]0) { $p | Add-PartitionAccessPath -AssignDriveLetter; $p = Get-Partition -DiskNumber $d.Number -PartitionNumber $p.PartitionNumber }
    $root = "$($p.DriveLetter):\"
    if (Test-Path (Join-Path $root 'Windows\System32\config\SYSTEM')) { $win = $root; break }
  }
  if (-not $win) { throw 'На диске не найден раздел с Windows (возможно, он зашифрован BitLocker)' }
  $sysFile = Join-Path $win 'Windows\System32\config\SYSTEM'
  $bak = "$sysFile.elasticmem.bak"
  if (-not (Test-Path $bak)) { Copy-Item $sysFile $bak; Write-Host "Резервная копия куста: $bak" }
  & reg.exe load $hive $sysFile | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'reg load не удался (диск используется?)' }
  $loaded = $true
  $cur = (Get-ItemProperty "Registry::$hive\Select").Current
  $cs = "Registry::$hive\ControlSet{0:D3}" -f $cur
  foreach ($svc in 'storahci', 'stornvme') {
    $k = "$cs\Services\$svc"
    if (Test-Path $k) {
      Set-ItemProperty $k -Name Start -Value 0
      if (Test-Path "$k\StartOverride") { Remove-Item "$k\StartOverride" -Recurse -Force }
      Write-Host "${svc}: загрузочный (Start=0)"
    }
  }
  Set-ItemProperty "$cs\Control\Terminal Server" -Name fDenyTSConnections -Value 0
  Write-Host 'RDP разрешён'
  $fw = "$cs\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules"
  $n = 0
  if (Test-Path $fw) {
    $rules = Get-ItemProperty $fw
    foreach ($prop in $rules.PSObject.Properties) {
      $v = "$($prop.Value)"
      if ($v -match 'Dir=In' -and $v -match 'LPort=3389' -and $v -match 'Active=FALSE') {
        Set-ItemProperty $fw -Name $prop.Name -Value ($v -replace 'Active=FALSE', 'Active=TRUE')
        $n++
      }
    }
  }
  Write-Host "Правила брандмауэра для RDP включены: $n"
  Write-Host 'Готово: диск подготовлен для QEMU.'
} finally {
  if ($loaded) { [gc]::Collect(); [gc]::WaitForPendingFinalizers(); & reg.exe unload $hive | Out-Null }
  if ($mounted) { Dismount-DiskImage -ImagePath $Vhdx | Out-Null }
}
