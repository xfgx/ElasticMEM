# Run with Windows PowerShell 5.1 (recommended), or pwsh for syntax-only checks.
param([string]$Root=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop';$failed=$false
foreach($file in Get-ChildItem -LiteralPath (Join-Path $Root 'windows') -Filter *.ps1){
 $tokens=$null;$errors=$null
 [Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)|Out-Null
 if($errors.Count){$failed=$true;Write-Host "FAIL $($file.Name)";$errors|ForEach-Object {Write-Host $_.Message}}else{Write-Host "PARSE PASS $($file.Name) (syntax only, not Hyper-V/RDS execution)"}
}
if($failed){exit 1}
