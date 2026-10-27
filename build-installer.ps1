# Builds dist\BarracudaTray-Kurulum.zip: the app as a few small framework-dependent files plus the
# install scripts. Everything heavy (.NET, VB-CABLE, HeSuVi profiles) is downloaded at install time.
# A plain publish needs no NuGet packages; single-file or RID-specific publishing would.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$out = Join-Path $PSScriptRoot 'dist\BarracudaTray'
Remove-Item (Join-Path $PSScriptRoot 'dist') -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish BarracudaTray.csproj -c Release -p:DebugType=None -o (Join-Path $out 'app')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Copy-Item installer\* $out
Compress-Archive (Join-Path $out '*') -DestinationPath (Join-Path $PSScriptRoot 'dist\BarracudaTray-Kurulum.zip') -Force
Get-Item dist\BarracudaTray-Kurulum.zip | Select-Object Name, @{ n = 'KB'; e = { [math]::Round($_.Length / 1KB) } }
