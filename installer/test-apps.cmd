@echo off
chcp 65001 >nul
rem Тест ElasticMem: откройте Telegram, Chrome, Steam, Яндекс Музыку, затем запустите этот файл (лучше от администратора).
cd /d "%~dp0"
set EXE=ElasticMem.exe
if not exist "%EXE%" set EXE=..\ElasticMem.exe
start "" /wait "%EXE%" --selftest --scan telegram,chrome,steam,yandex --trim --send --out "%USERPROFILE%\Desktop\elasticmem_report.txt"
echo Отчёт: %USERPROFILE%\Desktop\elasticmem_report.txt
pause
