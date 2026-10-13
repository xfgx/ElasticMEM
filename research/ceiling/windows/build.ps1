$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
  $dirty=& git status --porcelain --untracked-files=all
  if($dirty){throw 'Commit all source changes before building'}
  $commit=& git rev-parse HEAD
  & cmd.exe /c 'cl.exe /nologo /Bv /std:c++17 /O2 /EHsc /W4 vram_bench.cpp /Fe:vram_bench.exe /link d3d12.lib dxgi.lib > build.log 2>&1'
  $code=$LASTEXITCODE
  Get-Content build.log
  $exeHash=$null
  if($code -eq 0){$exeHash=(Get-FileHash -Algorithm SHA256 vram_bench.exe).Hash}
  @{
    Commit=$commit
    WindowsSDKVersion=$env:WindowsSDKVersion
    VCToolsVersion=$env:VCToolsVersion
    Platform=$env:VSCMD_ARG_TGT_ARCH
    CompilerPath=(Get-Command cl.exe).Source
    CompilerSHA256=(Get-FileHash -Algorithm SHA256 (Get-Command cl.exe).Source).Hash
    Flags='/nologo /Bv /std:c++17 /O2 /EHsc /W4 /link d3d12.lib dxgi.lib'
    ExitCode=$code
    SourceSHA256=(Get-FileHash -Algorithm SHA256 vram_bench.cpp).Hash
    ExeSHA256=$exeHash
  } | ConvertTo-Json | Set-Content -Encoding utf8 build-info.json
  if ($code -ne 0) { throw 'Native compilation failed' }
  if((& git rev-parse HEAD) -ne $commit -or (& git status --porcelain --untracked-files=all)){throw 'Source changed during build'}
} finally { Pop-Location }
