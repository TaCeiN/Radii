# Builds the release file: one self-contained exe that is both the program and its installer.
#   dist\Radii-Setup-<version>.exe     <- the file to upload to GitHub Releases
# Usage:  powershell -ExecutionPolicy Bypass -File tools\build-release.ps1 [-Version 1.0.0]
param([string]$Version = "")
$ErrorActionPreference = 'Stop'
$env:LIB = ''
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

if (-not $Version) {
    $Version = ([xml](Get-Content Radii.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$out = Join-Path $root 'dist'
$work = Join-Path $out 'publish'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

dotnet publish Radii.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:Version=$Version -o $work
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$setup = Join-Path $out "Radii-Setup-$Version.exe"
Copy-Item (Join-Path $work 'Radii.exe') $setup -Force
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
"$hash  $(Split-Path $setup -Leaf)" | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding ascii
Write-Host ("Built {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
Write-Host "SHA256 $hash"
