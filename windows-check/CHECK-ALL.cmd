@echo off
setlocal
cd /d "%~dp0"
echo ElasticMem one-click automatic checks.
echo Local memory capture of up to 4 same-family renderers, 16 MiB each.
echo No memory dumps or screenshots in ZIP; no uploads. Close other ElasticMem windows first.
echo Open Chrome/Edge tabs or Steam beforehand. Close games before GPU tests.
start /wait "" ElasticMem.exe --research auto --allow-memory-read --seconds 120 --out "%TEMP%\ElasticMem-auto-result.txt"
echo Exit code: %errorlevel%
type "%TEMP%\ElasticMem-auto-result.txt"
echo The automatic ZIP path is above. Review it, then send it manually.
pause
