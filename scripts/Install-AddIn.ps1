# Installs the Companion for SOLIDWORKS add-in for this PC.
# Run as administrator, with SOLIDWORKS closed:  right-click > Run with PowerShell is NOT enough;
# open "Windows PowerShell" as administrator and run:  & "<project folder>\scripts\Install-AddIn.ps1"
#
# What it does (and nothing else):
#   1. builds the add-in from this project folder (Release build),
#   2. registers it with Windows (RegAsm, the standard .NET tool) so SOLIDWORKS can find it,
#   3. adds it to SOLIDWORKS's add-in list (two registry keys, written by the add-in itself),
#   4. writes a line to the Companion's activity log.
# Undo it any time with Remove-AddIn.ps1.

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\SWCompanion.AddIn\SWCompanion.AddIn.csproj'
$dll     = Join-Path $root 'src\SWCompanion.AddIn\bin\Release\net48\SWCompanion.AddIn.dll'
$regasm  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
$logDir  = Join-Path $env:LOCALAPPDATA 'SWCompanion\logs'

function Write-ActionLog([string]$text) {
    New-Item -ItemType Directory -Force $logDir | Out-Null
    $now = Get-Date
    Add-Content -Path (Join-Path $logDir ("activity-{0:yyyy-MM-dd}.log" -f $now)) -Value ("{0:yyyy-MM-dd HH:mm:ss}  {1}" -f $now, $text) -Encoding UTF8
}

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { Write-Host 'Please run this from a PowerShell window opened as administrator.' -ForegroundColor Yellow; exit 1 }
if (Get-Process SLDWORKS -ErrorAction SilentlyContinue) { Write-Host 'Please close SOLIDWORKS first (the add-in file is locked while it runs).' -ForegroundColor Yellow; exit 1 }
if (-not (Test-Path $regasm)) { Write-Host "Can't find RegAsm (part of .NET Framework 4.8) at $regasm" -ForegroundColor Red; exit 1 }

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }

Write-Host 'Building the add-in...'
& $dotnet build $project -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { Write-ActionLog 'add-in install: build failed, nothing registered.'; exit 1 }

Write-Host 'Registering it so SOLIDWORKS can find it...'
& $regasm /codebase /nologo $dll
if ($LASTEXITCODE -ne 0) { Write-ActionLog 'add-in install: RegAsm failed.'; exit 1 }

Write-ActionLog "add-in install: registered $dll (registry: HKLM\SOFTWARE\SolidWorks\Addins and HKCU\Software\SolidWorks\AddInsStartup; was: not installed). Result: OK."
Write-Host ''
Write-Host 'Done. Start SOLIDWORKS; "Companion for SOLIDWORKS" should be ticked under Tools > Add-Ins.' -ForegroundColor Green