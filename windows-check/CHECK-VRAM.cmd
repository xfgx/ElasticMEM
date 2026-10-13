@echo off
setlocal
cd /d "%~dp0"
echo G3 OWN buffer GPU_UPLOAD / READBACK vs unbuffered disk file.
echo NOT a VRAM pagefile. Close games first. Temporary file is CREATE_NEW and DELETE_ON_CLOSE.
set /p "disk=Existing folder on the SSD to test (example C:\Temp): "
if not exist "%disk%\" (echo Folder does not exist. & pause & exit /b 2)
start /wait "" ElasticMem.exe --research g3 --scratch "%disk%" --pool 32 --iterations 100 --negative --out "%TEMP%\ElasticMem-vram-result.txt"
echo Exit code: %errorlevel% - unsupported hardware is recorded, not replaced with another heap.
echo Result: %TEMP%\ElasticMem-vram-result.txt
echo In the app use Laboratory - Export logs ZIP to send the results.
pause
