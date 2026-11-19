<#
.SYNOPSIS
    Builds ProGuard's MSI installer and, with -Portable, the portable zip.
.DESCRIPTION
    1. Publishes ProGuard.Engine, ProGuard.UI and ProGuard.SessionAgent self-contained for win-x64, one exe each
       (no .NET install needed on the target machine), and checks which .NET runtime they bundle (-MinRuntime).
    2. Stages them in one folder, <OutDir>\files: exactly what the MSI puts in Program Files\ProGuard.
    3. SIGNING HOOK: signs the exes (sign.ps1, which does nothing unless PROGUARD_SIGNING is set).
    4. Builds installer\ProGuard.Installer.wixproj into <OutDir>\ProGuard-<version>-x64.msi.
    5. SIGNING HOOK: signs the MSI.
    6. With -Portable: <OutDir>\ProGuard-<version>-portable-win-x64.zip, the same exes plus start/stop scripts.

    Needs the .NET 10 SDK only: the WiX Toolset comes from NuGet.
.PARAMETER Version
    "1.2.3", "v1.2.3" or "1.2.3-beta.1". The exes get it as their version; the MSI gets the 1.2.3 part (Windows
    Installer versions are numeric). Default: the v* tag on HEAD, else 0.0.<number of commits>, so successive local
    builds upgrade each other.
.PARAMETER MinRuntime
    The oldest .NET runtime the exes may bundle, e.g. 10.0.15. The build fails if any exe was published with an older
    runtime pack (that is, with an SDK older than that runtime's). The release workflow passes the latest .NET 10
    servicing release, so a release always ships the newest runtime patch. Default: no minimum (the version is still
    printed).
.PARAMETER Step
    All (default) does everything above. The release workflow runs it in two halves, so that only the signing steps
    between them see the signing secrets:
      Stage    steps 1 and 2 (and 3, a no-op without PROGUARD_SIGNING)
      Package  steps 4 to 6, from the files a Stage run left in <OutDir>\files (5 is a no-op without PROGUARD_SIGNING)
.EXAMPLE
    .\installer\build.ps1 -Version 1.0.0 -Portable
#>
param(
    [string]$Version,
    [string]$OutDir = (Join-Path $PSScriptRoot "..\artifacts\installer"),
    [switch]$Portable,
    [string]$MinRuntime,
    [ValidateSet("All", "Stage", "Package")][string]$Step = "All"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$files = Join-Path $OutDir "files"
$projects = "ProGuard.Engine", "ProGuard.UI", "ProGuard.SessionAgent"

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

# "10.0.15" or "10.0.0-rc.2.25502.107" -> [version] of the numeric part, for comparing.
function ConvertTo-RuntimeVersion([string]$text) {
    if ($text -notmatch '^(\d+\.\d+\.\d+)') { throw "'$text' isn't a .NET runtime version." }
    [version]$Matches[1]
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

if ($Step -ne "Package") {
    # --- 1. Publish ------------------------------------------------------------------------------------------------
    # Not compressed: the MSI's cabinet and the zip compress anyway, and an uncompressed single-file exe starts faster
    # and doesn't keep a decompressed copy in memory (the session agent runs in every session).
    # Native libraries (SQLite, WPF's) stay next to the exes instead of being extracted to %TEMP% at startup: the
    # engine runs as LocalSystem and must only load code from Program Files, which only administrators can change.
    # In CI the restore is locked to the packages.lock.json files (Directory.Build.props).
    $publishRoot = Join-Path $OutDir "publish"
    $runtimes = @{}
    foreach ($project in $projects) {
        $dir = Join-Path $publishRoot $project
        if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
        Write-Host "Publishing $project..." -ForegroundColor Cyan
        Invoke-Checked "dotnet publish $project" {
            dotnet publish (Join-Path $root "src\$project\$project.csproj") -c Release -r win-x64 --self-contained true `
                -p:PublishSingleFile=true -p:Version=$Version -o $dir -nologo
        }

        # Which runtime went into the exe: the self-contained publish's deps.json names the runtime packs it used
        # ("runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.15"). The exe embeds that file; the copy the
        # publish wrote it from is in obj.
        $deps = @(Get-ChildItem (Join-Path $root "src\$project\obj\Release") -Recurse -Filter "$project.deps.json" |
            Where-Object { $_.Directory.Name -eq "win-x64" } | Sort-Object LastWriteTimeUtc -Descending)
        if (-not $deps) { throw "No win-x64 $project.deps.json under src\$project\obj\Release after publishing." }
        $packs = [regex]::Matches([IO.File]::ReadAllText($deps[0].FullName),
            '"runtimepack\.(Microsoft\.[A-Za-z]+\.App)\.Runtime\.win-x64/([0-9][^"]*)"')
        if ($packs.Count -eq 0) { throw "$($deps[0].FullName) names no runtime pack, so the bundled runtime is unknown." }
        $runtimes[$project] = @{}
        foreach ($pack in $packs) { $runtimes[$project][$pack.Groups[1].Value] = $pack.Groups[2].Value }
        if (-not $runtimes[$project].ContainsKey("Microsoft.NETCore.App")) { throw "$project bundles no Microsoft.NETCore.App." }
        $described = ($runtimes[$project].GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name) $($_.Value)" }) -join ", "
        Write-Host "  $project bundles $described"
    }

    if ($MinRuntime) {
        $minimum = ConvertTo-RuntimeVersion $MinRuntime
        foreach ($project in $projects) {
            foreach ($pack in $runtimes[$project].GetEnumerator()) {
                if ((ConvertTo-RuntimeVersion $pack.Value) -lt $minimum) {
                    throw "$project bundles $($pack.Name) $($pack.Value), older than the required $MinRuntime. " +
                        "Build with a .NET SDK that carries runtime $MinRuntime or later (dotnet --list-sdks; " +
                        "https://dotnet.microsoft.com/download/dotnet/10.0)."
                }
            }
        }
        Write-Host "Bundled runtimes are $MinRuntime or later." -ForegroundColor Cyan
    }
    $netCore = $runtimes["ProGuard.Engine"]["Microsoft.NETCore.App"]
    foreach ($project in $projects) {
        if ($runtimes[$project]["Microsoft.NETCore.App"] -ne $netCore) {
            throw "The exes bundle different .NET runtimes (${project}: $($runtimes[$project]['Microsoft.NETCore.App']), ProGuard.Engine: ${netCore})."
        }
    }

    # --- 2. Stage --------------------------------------------------------------------------------------------------
    if (Test-Path $files) { Remove-Item -Recurse -Force $files }
    New-Item -ItemType Directory -Path $files | Out-Null

    $staged = @{}
    foreach ($project in $projects) {
        foreach ($file in Get-ChildItem -File (Join-Path $publishRoot $project)) {
            if ($file.Extension -eq ".pdb" -or $file.Name -eq "appsettings.Development.json") { continue }
            # The engine's project reference to the session agent copies a framework-dependent build of it (it would
            # need .NET installed). The agent's own self-contained publish provides the real one.
            if ($project -eq "ProGuard.Engine" -and $file.Name -like "ProGuard.SessionAgent.*") { continue }
            if ($file.Name -eq "packages.lock.json") { throw "$project published its packages.lock.json; Directory.Build.targets should keep it out." }
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

    # The license asks for its terms and the Required Notice to go with every copy; the third-party licenses ask the
    # same for theirs. THIRD-PARTY-NOTICES.txt gets the bundled runtime's own notices file appended, from the exact
    # runtime pack the exes were published with.
    Copy-Item (Join-Path $root "LICENSE") (Join-Path $files "LICENSE.txt")
    Copy-Item (Join-Path $root "NOTICE") (Join-Path $files "NOTICE.txt")
    $packages = $env:NUGET_PACKAGES
    if (-not $packages) {
        $line = dotnet nuget locals global-packages --list | Select-Object -First 1
        $packages = "$line" -replace '^global-packages:\s*', ''
    }
    $runtimeNotices = Join-Path $packages.Trim() "microsoft.netcore.app.runtime.win-x64\$netCore\THIRD-PARTY-NOTICES.TXT"
    if (-not (Test-Path $runtimeNotices)) { throw "The runtime pack's notices file isn't at $runtimeNotices." }
    $utf8 = New-Object System.Text.UTF8Encoding $false
    $notices = [IO.File]::ReadAllText((Join-Path $root "THIRD-PARTY-NOTICES.txt"), $utf8).TrimEnd() + "`r`n`r`n`r`n" +
        ".NET Runtime ${netCore}: THIRD-PARTY-NOTICES.TXT (from Microsoft.NETCore.App.Runtime.win-x64 $netCore)`r`n" +
        ("-" * 100) + "`r`n`r`n" + [IO.File]::ReadAllText($runtimeNotices, $utf8)
    [IO.File]::WriteAllText((Join-Path $files "THIRD-PARTY-NOTICES.txt"), $notices, $utf8)
    Write-Host "Staged $((Get-ChildItem $files).Count) files in $files" -ForegroundColor Cyan

    # --- 3. SIGNING HOOK: the exes -----------------------------------------------------------------------------------
    # Off by default. See sign.ps1 and docs/installer.md for switching it on.
    & (Join-Path $PSScriptRoot "sign.ps1") -Path (Get-ChildItem $files -Filter "ProGuard.*.exe").FullName
}

if ($Step -ne "Stage") {
    foreach ($exe in $projects) {
        if (-not (Test-Path (Join-Path $files "$exe.exe"))) { throw "$files has no $exe.exe: run with -Step Stage (or All) first." }
    }

    # --- 4. MSI ----------------------------------------------------------------------------------------------------
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

    # --- 5. SIGNING HOOK: the MSI ------------------------------------------------------------------------------------
    & (Join-Path $PSScriptRoot "sign.ps1") -Path $msi
    Write-Host "MSI: $msi" -ForegroundColor Green

    # --- 6. Portable zip -------------------------------------------------------------------------------------------
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
}
