# Активировать драйвер ElasticMemStore в disposable Windows VM (test-signing).
# ТОЛЬКО для одноразовой виртуалки со снимком. На обычном ПК работать НЕ будет и НЕ должен:
# test-signing требует выключенного Secure Boot, а это запрещено для рабочих машин.
# Запускать ПКМ -> "Запуск от имени администратора". Авто-elevation намеренно НЕ делаем.
param(
    [string]$Sys = "$PSScriptRoot\ElasticMemStore.sys",
    [string]$Cer = "$PSScriptRoot\ElasticMem-testsign-public.cer",
    [switch]$IAcceptDisposableVmOnly
)
$ErrorActionPreference = 'Stop'
function Admin { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
if (-not (Admin)) { Write-Host 'Запустите от имени администратора (ПКМ -> Запуск от имени администратора).' -ForegroundColor Red; exit 1 }
if (-not $IAcceptDisposableVmOnly) { Write-Host 'Это одноразовая VM? Перезапустите с -IAcceptDisposableVmOnly. На рабочем ПК не запускать.' -ForegroundColor Yellow; exit 1 }

# Secure Boot должен быть ВЫКЛЮЧЕН (иначе test-signing невозможен). Сами его НЕ трогаем.
try { if (Confirm-SecureBootUEFI) { Write-Host 'Secure Boot включён — test-signing невозможен. Это нормально для рабочего ПК; тогда нужна attestation-подпись Microsoft, а не этот путь.' -ForegroundColor Red; exit 2 } } catch {}
if (-not (Test-Path $Sys)) { throw "Нет файла драйвера: $Sys" }
if (-not (Test-Path $Cer)) { throw "Нет сертификата: $Cer (возьмите из артефактов driver-sign)" }

$bcd = (& bcdedit /enum '{current}') -join "`n"
if ($bcd -notmatch 'testsigning\s+(Yes|Да)') {
    Write-Host 'Включаю test-signing (нужна ОДНА перезагрузка), затем запустите этот скрипт снова.' -ForegroundColor Cyan
    & bcdedit /set '{current}' testsigning on | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'bcdedit отказал (Secure Boot включён?).' }
    Write-Host 'Перезагрузите VM и запустите скрипт ещё раз.' -ForegroundColor Green
    exit 0
}

Write-Host 'Импорт тестового сертификата в доверенные (только этой VM)…'
Import-Certificate -FilePath $Cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath $Cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
if ((Get-AuthenticodeSignature $Sys).Status -ne 'Valid') { throw 'Подпись драйвера недействительна в этой VM. Не устанавливаю.' }

$dir = Join-Path $env:ProgramData 'ElasticMemDriver'
New-Item -ItemType Directory -Force $dir | Out-Null
& icacls.exe $dir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
$dest = Join-Path $dir 'ElasticMemStore.sys'
Copy-Item -LiteralPath $Sys -Destination $dest -Force
if (Get-Service ElasticMemStore -ErrorAction SilentlyContinue) { & sc.exe stop ElasticMemStore | Out-Null; & sc.exe delete ElasticMemStore | Out-Null; Start-Sleep 1 }
& sc.exe create ElasticMemStore type= kernel start= demand error= normal binPath= "`"$dest`"" DisplayName= ElasticMemStore | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Не удалось создать службу.' }
& sc.exe start ElasticMemStore
if ($LASTEXITCODE -ne 0) { throw 'Windows отклонила загрузку драйвера. Защиты НЕ отключать. Соберите код ошибки и удалите службу Uninstall-Driver.ps1.' }
Get-Service ElasticMemStore | Format-Table -AutoSize
Write-Host 'Драйвер загружен в этой VM (demand-start). Проверка SYS-пути:' -ForegroundColor Green
$runner = Join-Path $PSScriptRoot '..\..\vmm-runner\ElasticMem.VmmRunner.exe'
if (Test-Path $runner) { & $runner --probe; & $runner --research-disposable synthetic 32 --store sys }
else { Write-Host 'Рядом нет vmm-runner; запустите вручную: ElasticMem.VmmRunner.exe --research-disposable synthetic 32 --store sys' }
