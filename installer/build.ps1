<#
.SYNOPSIS
    Builds ProGuard's MSI installer and, with -Portable, the portable zip.
.DESCRIPTION
    1. Publishes ProGuard.Engine, ProGuard.UI and ProGuard.SessionAgent self-contained for win-x64, one exe each
       (no .NET install needed on the target machine).
    2. Stages them in one folder, <OutDir>\files: exactly what the MSI puts in Program Files\ProGuard.
    3. SIGNING HOOK: signs the exes (sign.ps1, which does nothing unless PROGUARD_SIGNING is set).
    4. Builds installer\ProGuard.Installer.wixproj into <OutDir>\ProGuard-<version>-x64.msi.
    5. SIGNING HOOK: signs the MSI.
    6. With -Portable: <OutDir>\ProGuard-<version>-portable-win-x64.zip, the same exes plus start/stop scripts.

    Needs the .NET 8 SDK only: the WiX Toolset comes from NuGet.
.PARAMETER Version
    "1.2.3", "v1.2.3" or "1.2.3-beta.1". The exes get it as their version; the MSI gets the 1.2.3 part (Windows
    Installer versions are numeric). Default: the v* tag on HEAD, else 0.0.<number of commits>, so successive local
    builds upgrade each other.
.EXAMPLE
    .\installer\build.ps1 -Version 1.0.0 -Portable
#>
param(
    [string]$Version,
    [string]$OutDir = (Join-Path $PSScriptRoot "..\artifacts\installer"),
    [switch]$Portable
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

# --- Version -------------------------------------------------------------------------------------------------------
if (-not $Version) {
    $tag = git -C $root describe --tags --exact-match --match "v[0-9]*" HEAD 2>$null
    $Version = if ($LASTEXITCODE -eq 0 -and $tag) { $tag } else { "0.0.$(git -C $root rev-list --count HEAD)" }
}
$Version = $Version.Trim() -replace '^v', ''
if ($Version -notmatch '^(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.-]+)?$') {
    throw "Version '$Version' isn't major.minor.patch with an optional -prerelease suffix."
}
# Windows Installer limits: major and minor up to 255, build up to 65535. Only these three fields count for upgrades.
if ([int]$Matches[1] -gt 255 -or [int]$Matches[2] -gt 255 -or [int]$Matches[3] -gt 65535) {
    throw "Version '$Version' is out of range for Windows Installer (255.255.65535 at most)."
}
$msiVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3])"
Write-Host "Version $Version (MSI $msiVersion)" -ForegroundColor Cyan

# --- 1. Publish ----------------------------------------------------------------------------------------------------
# Not compressed: the MSI's cabinet and the zip compress anyway, and an uncompressed single-file exe starts faster
# and doesn't keep a decompressed copy in memory (the session agent runs in every session).
# Native libraries (SQLite, WPF's) stay next to the exes instead of being extracted to %TEMP% at startup: the
# engine runs as LocalSystem and must only load code from Program Files, which only administrators can change.
$publishRoot = Join-Path $OutDir "publish"
$projects = "ProGuard.Engine", "ProGuard.UI", "ProGuard.SessionAgent"
foreach ($project in $projects) {
    $dir = Join-Path $publishRoot $project
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    Write-Host "Publishing $project..." -ForegroundColor Cyan
    Invoke-Checked "dotnet publish $project" {
        dotnet publish (Join-Path $root "src\$project\$project.csproj") -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:Version=$Version -o $dir -nologo
    }
}

# --- 2. Stage ------------------------------------------------------------------------------------------------------
$files = Join-Path $OutDir "files"
if (Test-Path $files) { Remove-Item -Recurse -Force $files }
New-Item -ItemType Directory -Path $files | Out-Null

$staged = @{}
foreach ($project in $projects) {
    foreach ($file in Get-ChildItem -File (Join-Path $publishRoot $project)) {
        if ($file.Extension -eq ".pdb" -or $file.Name -eq "appsettings.Development.json") { continue }
        # The engine's project reference to the session agent copies a framework-dependent build of it (it would
        # need .NET installed). The agent's own self-contained publish provides the real one.
        if ($project -eq "ProGuard.Engine" -and $file.Name -like "ProGuard.SessionAgent.*") { continue }
        $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
        if ($staged.ContainsKey($file.Name)) {
            if ($staged[$file.Name] -ne $hash) {
                throw "$($file.Name) comes from more than one publish with different contents; they can't share a folder."
            }
            continue
        }
        $staged[$file.Name] = $hash
        Copy-Item $file.FullName $files
    }
}
foreach ($exe in $projects) {
    if (-not (Test-Path (Join-Path $files "$exe.exe"))) { throw "$exe.exe is missing from the staged files." }
}
# A runtimeconfig.json means a framework-dependent exe slipped in.
if (Get-ChildItem $files -Filter "*.runtimeconfig.json") { throw "A framework-dependent exe was staged: $files has a runtimeconfig.json." }
# The license asks for its terms and the Required Notice to go with every copy.
Copy-Item (Join-Path $root "LICENSE") (Join-Path $files "LICENSE.txt")
Copy-Item (Join-Path $root "NOTICE") (Join-Path $files "NOTICE.txt")
Write-Host "Staged $((Get-ChildItem $files).Count) files in $files" -ForegroundColor Cyan

# --- 3. SIGNING HOOK: the exes ---------------------------------------------------------------------------------------
# Off by default. See sign.ps1 and docs/installer.md for switching it on.
& (Join-Path $PSScriptRoot "sign.ps1") -Path (Get-ChildItem $files -Filter "ProGuard.*.exe").FullName

# --- 4. MSI --------------------------------------------------------------------------------------------------------
# The license agreement dialog shows LICENSE (with NOTICE) as RTF, generated here so it never drifts from LICENSE.
$rtf = Join-Path $OutDir "License.rtf"
& (Join-Path $PSScriptRoot "ConvertTo-LicenseRtf.ps1") -Path (Join-Path $root "LICENSE"), (Join-Path $root "NOTICE") -Destination $rtf

$msiName = "ProGuard-$Version-x64"
$msiOut = Join-Path $OutDir "msi"
if (Test-Path $msiOut) { Remove-Item -Recurse -Force $msiOut }
Write-Host "Building $msiName.msi..." -ForegroundColor Cyan
Invoke-Checked "Building the MSI" {
    dotnet build (Join-Path $PSScriptRoot "ProGuard.Installer.wixproj") -c Release -nologo `
        "-p:ProductVersion=$msiVersion" "-p:StagingDir=$files" "-p:LicenseRtf=$rtf" "-p:OutputName=$msiName" `
        "-p:OutputPath=$msiOut\" "-p:IntermediateOutputPath=$(Join-Path $OutDir 'obj')\"
}
$msi = Join-Path $OutDir "$msiName.msi"
Copy-Item (Join-Path $msiOut "$msiName.msi") $msi -Force

# --- 5. SIGNING HOOK: the MSI ----------------------------------------------------------------------------------------
& (Join-Path $PSScriptRoot "sign.ps1") -Path $msi
Write-Host "MSI: $msi" -ForegroundColor Green

# --- 6. Portable zip -----------------------------------------------------------------------------------------------
if ($Portable) {
    $portableRoot = Join-Path $OutDir "portable"
    $portableDir = Join-Path $portableRoot "ProGuard"
    if (Test-Path $portableRoot) { Remove-Item -Recurse -Force $portableRoot }
    New-Item -ItemType Directory -Path $portableDir | Out-Null
    Copy-Item (Join-Path $files "*") $portableDir
    Copy-Item (Join-Path $PSScriptRoot "portable\*") $portableDir
    $zip = Join-Path $OutDir "ProGuard-$Version-portable-win-x64.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path $portableDir -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "Portable zip: $zip" -ForegroundColor Green
}
