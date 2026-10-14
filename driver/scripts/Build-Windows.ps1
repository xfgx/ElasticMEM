param([ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not (Get-Command msbuild.exe -ErrorAction SilentlyContinue)) { throw 'Run in VS 2022 Developer PowerShell with Desktop C++ + Windows SDK/WDK 10.0.26100 installed.' }
& msbuild.exe "$root\driver\kmdf\ElasticMemStore.vcxproj" /m /t:Rebuild "/p:Configuration=$Configuration" /p:Platform=x64 /p:WindowsTargetPlatformVersion=10.0.26100.0
if ($LASTEXITCODE -ne 0) { throw 'WDK build failed. No driver should be installed.' }
& dotnet publish "$root\driver\client\ElasticMem.DriverClient.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$root\driver\out\client"
if ($LASTEXITCODE -ne 0) { throw 'Driver client build failed.' }
Write-Host 'UNSIGNED research build only. Sign locally using your approved driver-signing process; validate in a disposable Windows 11 VM. No security settings were changed.'
