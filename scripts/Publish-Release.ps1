# Builds the download people install: a ready-to-run Companion for SOLIDWORKS folder, zipped.
# Self-contained: .NET is included, so nothing else has to be installed. SOLIDWORKS's own API files are NOT
# included: the Companion loads them from the SOLIDWORKS installed on each PC.
#
# Run from the project folder:   .\scripts\Publish-Release.ps1
# Result:                         dist\Companion-for-SOLIDWORKS-<version>-win-x64.zip
param([string]$Version = "")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if (-not $Version) { $Version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version }
$name = "Companion-for-SOLIDWORKS-$Version-win-x64"
$dist = Join-Path $root "dist"
$out = Join-Path $dist $name

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Write-Host "Building $name ..."
dotnet publish (Join-Path $root "src\SWCompanion.App\SWCompanion.App.csproj") -c Release -r win-x64 --self-contained true `
    -p:DebugType=None -p:DebugSymbols=false -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "The build failed." }

# Leave out SOLIDWORKS's API files (they come from each PC's own SOLIDWORKS installation).
Get-ChildItem $out -Filter "SolidWorks.Interop.*.dll" | Remove-Item -Force

# A short "start here" note next to the program.
@"
Companion for SOLIDWORKS $Version

To start: double-click SWCompanion.exe.

The first time, Windows may say "Windows protected your PC". The Companion is free and not (yet) signed with a
paid code-signing certificate, so Windows doesn't recognise it. Click "More info", then "Run anyway".

It needs SOLIDWORKS installed on this PC. It only watches and explains; it never changes SOLIDWORKS or your files.

Full instructions, questions and updates: see README.md in the download page on GitHub.
Created by Phillip Lewis, Phillip.Lewis@converge.design
"@ | Set-Content (Join-Path $out "START HERE.txt") -Encoding utf8

$zip = Join-Path $dist "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $out -DestinationPath $zip
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Done: $zip ($size MB)" -ForegroundColor Green
