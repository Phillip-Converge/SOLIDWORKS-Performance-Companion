# Removes the Companion for SOLIDWORKS add-in from this PC.
# Run as administrator, with SOLIDWORKS closed. Leaves the project files and logs alone.

$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent $PSScriptRoot
$dll    = Join-Path $root 'src\SWCompanion.AddIn\bin\Release\net48\SWCompanion.AddIn.dll'
$regasm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
$logDir = Join-Path $env:LOCALAPPDATA 'SWCompanion\logs'

function Write-ActionLog([string]$text) {
    New-Item -ItemType Directory -Force $logDir | Out-Null
    $now = Get-Date
    Add-Content -Path (Join-Path $logDir ("activity-{0:yyyy-MM-dd}.log" -f $now)) -Value ("{0:yyyy-MM-dd HH:mm:ss}  {1}" -f $now, $text) -Encoding UTF8
}

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { Write-Host 'Please run this from a PowerShell window opened as administrator.' -ForegroundColor Yellow; exit 1 }
if (Get-Process SLDWORKS -ErrorAction SilentlyContinue) { Write-Host 'Please close SOLIDWORKS first.' -ForegroundColor Yellow; exit 1 }
if (-not (Test-Path $dll)) { Write-Host "The add-in file isn't at $dll, so there's nothing to unregister from here." -ForegroundColor Yellow; exit 1 }

& $regasm /unregister /nologo $dll
if ($LASTEXITCODE -ne 0) { Write-ActionLog 'add-in removal: RegAsm failed.'; exit 1 }
Write-ActionLog "add-in removal: unregistered $dll (registry keys removed; was: installed). Result: OK."
Write-Host 'Removed. SOLIDWORKS will no longer list or load the Companion add-in.' -ForegroundColor Green