@echo off
chcp 65001 >nul
rem Проверка постраничного сжатия АКТИВНОГО приложения на вашем ПК. Без драйвера, VM и прав администратора.
rem Ничего не устанавливает. Прервать в любой момент: Ctrl+C (вытесненные страницы просто вернутся при обращении).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0user-test.ps1" %*
pause
