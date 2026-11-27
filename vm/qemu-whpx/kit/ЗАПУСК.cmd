@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo ElasticMem: Windows-гость в QEMU/WHPX и сжатие его памяти
echo.
echo  0 - ПОЛНАЯ ПРОВЕРКА одной кнопкой (рекомендуется): гость с пейджером, замеры, zip-отчёт
echo  9 - Полная проверка БЕЗ пейджера (для сравнения, если гость падает)
echo  1 - Запустить гостя (обычный QEMU, дорожка 1)
echo  2 - Запустить гостя с пейджером em-pager (дорожка 2)
echo  3 - Открыть приложение гостя окном (RemoteApp)
echo  4 - Замер дорожки 1 (агент вытесняет холодную память qemu)
echo  5 - Замер дорожки 2 (журнал пейджера)
echo  6 - Gate 0 по памяти гостя (zero/dedup/delta)
echo  7 - Gate 0 по приложениям на этом ПК (без ВМ)
echo.
set /p c=Выбор: 
if "%c%"=="0" powershell -NoProfile -ExecutionPolicy Bypass -File Run-All.ps1 & goto end
if "%c%"=="9" powershell -NoProfile -ExecutionPolicy Bypass -File Run-All.ps1 -Mode plain & goto end
if "%c%"=="1" goto s1
if "%c%"=="2" goto s2
if "%c%"=="3" goto s3
if "%c%"=="4" powershell -NoProfile -ExecutionPolicy Bypass -File Measure.ps1 -Mode track1
if "%c%"=="5" powershell -NoProfile -ExecutionPolicy Bypass -File Measure.ps1 -Mode track2
if "%c%"=="6" powershell -NoProfile -ExecutionPolicy Bypass -File Measure.ps1 -Mode pagestat
if "%c%"=="7" powershell -NoProfile -ExecutionPolicy Bypass -File Measure.ps1 -Mode host
goto end
:s1
set /p v=Путь к .vhdx гостя: 
powershell -NoProfile -ExecutionPolicy Bypass -File Start-Guest.ps1 -Vhdx "%v%" -Mode plain
goto end
:s2
set /p v=Путь к .vhdx гостя: 
powershell -NoProfile -ExecutionPolicy Bypass -File Start-Guest.ps1 -Vhdx "%v%" -Mode pager
goto end
:s3
set /p r=Путь к вашему .rdp (Enter - указать программу): 
if not "%r%"=="" powershell -NoProfile -ExecutionPolicy Bypass -File Open-App.ps1 -Rdp "%r%" & goto end
set g=||ElasticMem-Edge
set /p g=Программа в госте (Enter - Microsoft Edge): 
powershell -NoProfile -ExecutionPolicy Bypass -File Open-App.ps1 -Program "%g%"
:end
pause
