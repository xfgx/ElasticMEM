# Подготовка диска гостя С ХОСТА, без загрузки гостя в Hyper-V (вместо Prepare-Guest.ps1). Нужны права администратора,
# ВМ должна быть выключена. Делает то же, что Prepare-Guest.ps1, но офлайн, в реестре SYSTEM внутри .vhdx:
#  * загрузочные драйверы AHCI/NVMe (storahci, stornvme: Start=0) — иначе в QEMU INACCESSIBLE_BOOT_DEVICE;
#  * разрешает RDP (fDenyTSConnections=0) и включает входящие правила брандмауэра для порта 3389;
#  * драйверы virtio (сеть NetKVM, диск viostor, balloon) из virtio-win — офлайн через DISM, как у Hyper-V свои
#    синтетические устройства: с ними QEMU не эмулирует e1000e/AHCI по регистрам; служба balloon (blnsvr) для
#    статистики памяти гостя. Печатает VIRTIO=ok|fail|absent.
# Перед изменением копирует куст SYSTEM рядом (SYSTEM.elasticmem.bak). Диск после работы отключается.
param([Parameter(Mandatory = $true)][string]$Vhdx, [switch]$DisableAutoLogon)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Запустите от администратора' }
if (-not (Test-Path $Vhdx)) { throw "Нет файла $Vhdx" }
$Vhdx = (Resolve-Path $Vhdx).Path
$hive = 'HKLM\EMGUEST'
$swHive = 'HKLM\EMGUESTSW'
$swLoaded = $false
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
  # ---------- драйверы virtio (DISM работает, пока кусты не загружены) ----------
  $virtio = Join-Path $PSScriptRoot 'virtio'
  $virtioOk = $false
  $swFile0 = Join-Path $win 'Windows\System32\config\SOFTWARE'
  if ((Test-Path $virtio) -and (Test-Path $swFile0)) {
    & reg.exe load HKLM\EMGUESTV $swFile0 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
      $cv0 = Get-ItemProperty 'Registry::HKLM\EMGUESTV\Microsoft\Windows NT\CurrentVersion' -ErrorAction SilentlyContinue
      $build = [int]"0$($cv0.CurrentBuild)"; $itype = "$($cv0.InstallationType)"
      [gc]::Collect(); [gc]::WaitForPendingFinalizers(); & reg.exe unload HKLM\EMGUESTV | Out-Null
      $os = if ($itype -eq 'Client') { if ($build -ge 22000) { 'w11' } else { 'w10' } } else { if ($build -ge 26100) { '2k25' } elseif ($build -ge 20348) { '2k22' } else { '2k19' } }
      $fails = 0
      foreach ($drv in 'NetKVM', 'viostor', 'Balloon') {
        $dir = Join-Path $virtio "$drv\$os"
        $o = & dism.exe /English /Image:$win /Add-Driver /Driver:$dir /Recurse 2>&1
        if ($LASTEXITCODE -ne 0) { $fails++; Write-Host "virtio $drv ($os): DISM код $LASTEXITCODE — $((@($o) | Select-Object -Last 3) -join ' ')" } else { Write-Host "virtio $drv ($os): установлен" }
      }
      # служба balloon: статистика памяти гостя для автоматического размера balloon (как Dynamic Memory Hyper-V)
      $bdir = Join-Path $win 'Program Files\ElasticMem'
      New-Item -ItemType Directory -Force $bdir | Out-Null
      Copy-Item (Join-Path $virtio "Balloon\$os\blnsvr.exe") $bdir -Force
      $virtioOk = $fails -eq 0
    }
  }
  if (-not (Test-Path $virtio)) { Write-Host 'VIRTIO=absent' } elseif ($virtioOk) { Write-Host 'VIRTIO=ok' } else { Write-Host 'VIRTIO=fail' }
  & reg.exe load $hive $sysFile | Out-Null
  if ($LASTEXITCODE -ne 0) { throw 'reg load не удался (диск используется?)' }
  $loaded = $true
  $cur = (Get-ItemProperty "Registry::$hive\Select").Current
  $cs = "Registry::$hive\ControlSet{0:D3}" -f $cur
  if ($virtioOk) {
    # viostor — загрузочный (диск virtio-blk), служба BalloonService (blnsvr.exe) — автозапуск от LocalSystem
    if (Test-Path "$cs\Services\viostor") { Set-ItemProperty "$cs\Services\viostor" -Name Start -Value 0 }
    $bs = "$cs\Services\BalloonService"
    if (-not (Test-Path $bs)) { New-Item $bs -Force | Out-Null }
    Set-ItemProperty $bs -Name Type -Value 0x10 -Type DWord
    Set-ItemProperty $bs -Name Start -Value 2 -Type DWord
    Set-ItemProperty $bs -Name ErrorControl -Value 1 -Type DWord
    Set-ItemProperty $bs -Name ImagePath -Value '"C:\Program Files\ElasticMem\blnsvr.exe"' -Type ExpandString
    Set-ItemProperty $bs -Name ObjectName -Value 'LocalSystem' -Type String
    Set-ItemProperty $bs -Name DisplayName -Value 'Balloon Service (ElasticMem)' -Type String
    Write-Host 'virtio: viostor загрузочный, служба BalloonService включена'
  }
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
  # RemoteApp: окно одной программы гостя (браузера) вместо всего рабочего стола. В клиентских Windows список
  # разрешённых программ пуст — разрешаем запуск любой программы по пути (fDisabledAllowList=1, куст SOFTWARE).
  $swFile = Join-Path $win 'Windows\System32\config\SOFTWARE'
  if (Test-Path $swFile) { & reg.exe load $swHive $swFile 2>$null | Out-Null }
  if ((Test-Path $swFile) -and $LASTEXITCODE -eq 0) {
    $swLoaded = $true
    $cv = Get-ItemProperty "Registry::$swHive\Microsoft\Windows NT\CurrentVersion" -ErrorAction SilentlyContinue
    Write-Host "Гость: $($cv.ProductName) $($cv.DisplayVersion) (сборка $($cv.CurrentBuild)), тип установки: $($cv.InstallationType)"
    $k = "Registry::$swHive\Microsoft\Windows NT\CurrentVersion\Terminal Server\TSAppAllowList"
    if (-not (Test-Path $k)) { New-Item $k -Force | Out-Null }
    Set-ItemProperty $k -Name fDisabledAllowList -Value 1 -Type DWord
    $pol = "Registry::$swHive\Policies\Microsoft\Windows NT\Terminal Services"
    if (-not (Test-Path $pol)) { New-Item $pol -Force | Out-Null }
    Set-ItemProperty $pol -Name fAllowUnlistedRemotePrograms -Value 1 -Type DWord
    Write-Host 'RemoteApp: разрешено окно отдельной программы (fDisabledAllowList=1, fAllowUnlistedRemotePrograms=1)'
    # Edge как опубликованная программа RemoteApp с псевдонимом ElasticMem-Edge (как в бэкенде Hyper-V 0.27:
    # remoteapplicationprogram:s:||ElasticMem-Edge). Путь ищется на диске гостя — у разных гостей Edge в разных папках.
    $edgeRel = @('Program Files (x86)\Microsoft\Edge\Application\msedge.exe', 'Program Files\Microsoft\Edge\Application\msedge.exe') |
      Where-Object { Test-Path -LiteralPath (Join-Path $win $_) -PathType Leaf } | Select-Object -First 1
    if ($edgeRel) {
      $edgeGuest = 'C:\' + $edgeRel
      $ak = "$k\Applications\ElasticMem-Edge"
      if (-not (Test-Path $ak)) { New-Item $ak -Force | Out-Null }
      foreach ($p in @(@('Name', 'Microsoft Edge', 'String'), @('Path', $edgeGuest, 'String'), @('VPath', $edgeGuest, 'String'),
          @('IconPath', $edgeGuest, 'String'), @('IconIndex', 0, 'DWord'), @('CommandLineSetting', 1, 'DWord'),
          @('RequiredCommandLine', '', 'String'), @('ShowInTSWA', 0, 'DWord'))) {
        Set-ItemProperty $ak -Name $p[0] -Value $p[1] -Type $p[2]
      }
      Write-Host "EDGE_GUEST_PATH=$edgeGuest"
      Write-Host 'RemoteApp: Edge опубликован как ElasticMem-Edge'
    } else {
      Write-Host 'EDGE_GUEST_PATH='
      Write-Host 'RemoteApp: Edge на диске гостя не найден (Server Core или Edge не установлен) — окно только Edge недоступно'
    }
    # Автовход: Windows сам входит в рабочий стол при загрузке. В клиентской Windows один сеанс на пользователя,
    # и RemoteApp подключается к этому уже открытому рабочему столу — окно программы не появляется.
    $wl = "Registry::$swHive\Microsoft\Windows NT\CurrentVersion\Winlogon"
    $auto = "$((Get-ItemProperty $wl -ErrorAction SilentlyContinue).AutoAdminLogon)"
    if ($auto -eq '1' -and $DisableAutoLogon) {
      Set-ItemProperty $wl -Name AutoAdminLogon -Value '0'
      Write-Host 'Автовход в гостя отключён (AutoAdminLogon=0)'
    } else { Write-Host "AutoAdminLogon=$(if ($auto) { $auto } else { '0' })" }
  } else { Write-Host 'RemoteApp: куст SOFTWARE не загрузился — окно отдельной программы может не открыться' }
  Write-Host 'Готово: диск подготовлен для QEMU.'
} finally {
  if ($loaded -or $swLoaded) { [gc]::Collect(); [gc]::WaitForPendingFinalizers() }
  if ($swLoaded) { & reg.exe unload $swHive | Out-Null }
  if ($loaded) { & reg.exe unload $hive | Out-Null }
  if ($mounted) { Dismount-DiskImage -ImagePath $Vhdx | Out-Null }
}
