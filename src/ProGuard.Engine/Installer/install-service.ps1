<#
.SYNOPSIS
    Installs (or with -Uninstall removes) the ProGuard engine as a LocalSystem Windows service.
    For development. Users install ProGuard with its MSI (installer\build.ps1, docs\installer.md), which installs the
    same service; this script refuses to run while the MSI is installed.
.DESCRIPTION
    Publish first:  dotnet publish src\ProGuard.Engine -c Release
    That publishes the session agent (ProGuard.SessionAgent.exe) with the engine. The service starts one in every
    signed-in user's session, as that user, to report the session's foreground program and idle time.
    The published files are copied to -InstallDir (default "%ProgramFiles%\ProGuard\Engine") and the service runs
    from there. It must not run from a build folder: the service is LocalSystem, so anyone who can write to its
    directory could replace the exe or a DLL and run code as SYSTEM. Program Files is writable only by admins.
    Must be run elevated.
#>
param(
    [switch]$Uninstall,
    [string]$SourceDir = (Join-Path $PSScriptRoot "..\bin\Release\net8.0-windows\win-x64\publish"),
    [string]$InstallDir = (Join-Path $env:ProgramFiles "ProGuard\Engine")
)

$ErrorActionPreference = "Stop"
$serviceName = "ProGuardEngine"
$agentName = "ProGuard.SessionAgent"

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Run this script from an elevated PowerShell."
    exit 1
}

# The MSI (installer\, docs\installer.md) owns the same service name. Replacing or deleting its service from here would
# leave Windows Installer with a product whose service is gone, so don't: uninstall the MSI first.
$msiUpgradeCode = "{276E7864-81FA-4F30-AC57-41896E15D3C6}"
$windowsInstaller = New-Object -ComObject WindowsInstaller.Installer
$msiProducts = $windowsInstaller.GetType().InvokeMember("RelatedProducts", "GetProperty", $null, $windowsInstaller, @($msiUpgradeCode))
if ($msiProducts.GetType().InvokeMember("Count", "GetProperty", $null, $msiProducts, $null) -gt 0) {
    Write-Error "ProGuard is installed from its MSI. Uninstall it first (Settings > Apps > ProGuard), then run this script."
    exit 1
}

function Remove-ProGuardService {
    $existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if (-not $existing) { return }
    if ($existing.Status -ne "Stopped") {
        Write-Host "Stopping $serviceName..."
        Stop-Service -Name $serviceName -Force
        $existing.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }
    Write-Host "Removing $serviceName..."
    sc.exe delete $serviceName | Out-Null
    # The SCM deletes the service once every handle to it is closed.
    for ($i = 0; $i -lt 20 -and (Get-Service -Name $serviceName -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
}

# The service ends its session agents when it stops. One left behind by a service that crashed would keep the
# installed files in use, so end any that remain, in every session (taskkill can end other users' processes).
# Matched by name only: an elevated shell can't read the path of another user's process.
function Stop-SessionAgents {
    if (-not (Get-Process -Name $agentName -ErrorAction SilentlyContinue)) { return }
    Write-Host "Ending leftover session agents..."
    taskkill.exe /F /IM "$agentName.exe" 2>&1 | Out-Null
    for ($i = 0; $i -lt 20 -and (Get-Process -Name $agentName -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
}

if ($Uninstall) {
    Remove-ProGuardService
    Stop-SessionAgents
    if (Test-Path $InstallDir) {
        Write-Host "Deleting $InstallDir..."
        Remove-Item -Recurse -Force $InstallDir
        # The default location's "ProGuard" parent folder, once nothing else is in it.
        $parent = Split-Path -Parent $InstallDir
        if ((Split-Path -Leaf $parent) -eq "ProGuard" -and -not (Get-ChildItem -Force $parent)) { Remove-Item -Force $parent }
    }
    Write-Host "Uninstalled. The database and logs in $env:ProgramData\ProGuard were kept."
    exit 0
}

$sourceExe = Join-Path $SourceDir "ProGuard.Engine.exe"
if (-not (Test-Path $sourceExe)) {
    Write-Error "$sourceExe not found. Publish first: dotnet publish src\ProGuard.Engine -c Release"
    exit 1
}

$sourceAgent = Join-Path $SourceDir "$agentName.exe"
if (-not (Test-Path $sourceAgent)) {
    Write-Error "$sourceAgent not found: the engine's publish output should include it. Publish again: dotnet publish src\ProGuard.Engine -c Release"
    exit 1
}

# Reinstalling over a running service: stop it (and its session agents) first so its files can be replaced.
Remove-ProGuardService
Stop-SessionAgents

Write-Host "Copying $SourceDir to $InstallDir..."
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $SourceDir "*") -Destination $InstallDir -Recurse -Force
$exePath = Join-Path $InstallDir "ProGuard.Engine.exe"

Write-Host "Installing $serviceName..."
sc.exe create $serviceName binPath= "`"$exePath`"" start= auto DisplayName= "ProGuard Engine" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Error "sc.exe create failed ($LASTEXITCODE)"; exit 1 }
sc.exe description $serviceName "ProGuard Engine Service for process management." | Out-Null
# Restart after a crash: 5 s, 10 s, then every 60 s; reset the count after a day without failures.
sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/10000/restart/60000 | Out-Null

Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
Write-Host "Done! Log: $env:ProgramData\ProGuard\logs\engine.log"
