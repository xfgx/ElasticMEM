@echo off
setlocal
cd /d "%~dp0"
echo ElasticMem Windows checks: environment, selftest, process counters, typed store, GPU probe.
echo No foreign-process memory read, trimming, MMAgent changes or uploads.
start /wait "" ElasticMem.exe --research suite --seconds 15 --out "%TEMP%\ElasticMem-check-result.txt"
echo Suite exit code: %errorlevel%
echo Result: %TEMP%\ElasticMem-check-result.txt
echo Creating a logs-only ZIP in TEMP. Inspect it before sending.
set "zip=%TEMP%\ElasticMem-logs-%RANDOM%.zip"
start /wait "" ElasticMem.exe --research export --out "%zip%"
echo Export exit code: %errorlevel%
echo ZIP: %zip%
echo Open the app - Laboratory - UI check for screenshots at your DPI.
pause
