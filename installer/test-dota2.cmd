@echo off
chcp 65001 >nul
rem Dota 2: запускайте ТОЛЬКО в главном меню / офлайн (VAC). Без --trim, только чтение.
cd /d "%~dp0"
set EXE=ElasticMem.exe
if not exist "%EXE%" set EXE=..\ElasticMem.exe
start "" /wait "%EXE%" --scan dota2 --out "%USERPROFILE%\Desktop\elasticmem_dota2.txt"
echo Отчёт: %USERPROFILE%\Desktop\elasticmem_dota2.txt
pause
