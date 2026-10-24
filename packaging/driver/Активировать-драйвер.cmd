@echo off
rem Активация драйвера ElasticMemStore в ОДНОРАЗОВОЙ Windows VM (test-signing).
rem Для обычного ПК НЕ предназначено: там нужна attestation-подпись Microsoft, а не этот путь.
rem Запускать: ПКМ -> "Запуск от имени администратора". Авто-elevation намеренно не делаем.
setlocal
net session >nul 2>&1
if errorlevel 1 (
  echo Запустите этот файл от имени администратора: ПКМ -^> "Запуск от имени администратора".
  pause
  exit /b 1
)
echo.
echo ВНИМАНИЕ: это только для одноразовой виртуальной машины со снимком.
echo На рабочем ПК драйвер так не загрузить - нужна подпись Microsoft (см. ATTESTATION.md).
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Activate-DisposableVM.ps1" -IAcceptDisposableVmOnly
pause
