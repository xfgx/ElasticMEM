# Запускать ВНУТРИ гостя (в ВМ Hyper-V) от администратора ОДИН раз перед переносом диска в QEMU.
# Делает только штатные вещи: включает загрузочные драйверы AHCI/NVMe (в Hyper-V диск висит на VMBus/storvsc,
# в QEMU — на AHCI; без этого Windows уйдёт в INACCESSIBLE_BOOT_DEVICE) и включает RDP для окна приложения.
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Запустите от администратора' }
foreach ($svc in 'storahci', 'stornvme') {
  $k = "HKLM:\SYSTEM\CurrentControlSet\Services\$svc"
  if (Test-Path $k) {
    Set-ItemProperty $k -Name Start -Value 0
    if (Test-Path "$k\StartOverride") { Remove-Item "$k\StartOverride" -Recurse -Force }
    Write-Host ($svc + ': загрузочный (Start=0)')
  }
}
Set-ItemProperty 'HKLM:\System\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -Value 0
Enable-NetFirewallRule -DisplayGroup 'Remote Desktop' -ErrorAction SilentlyContinue
Enable-NetFirewallRule -DisplayGroup 'Удаленный рабочий стол' -ErrorAction SilentlyContinue
Write-Host 'RDP включён.'
$mc = (Get-MMAgent).MemoryCompression
Write-Host "Memory Compression в госте: $mc. Для дорожки 2 лучше выключить его в госте (Disable-MMAgent -MemoryCompression): иначе гость сам сжимает свою память, и нашему хранилищу достаётся меньше."
Write-Host 'Готово. Выключите гостя (Stop-Computer) и запускайте Start-Guest.ps1 на хосте.'
