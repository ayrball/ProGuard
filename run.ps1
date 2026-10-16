# Starts ProGuard: the engine (unless this checkout's engine is already running), then the UI.
#   .\run.ps1            run what's built (builds first if nothing is)
#   .\run.ps1 -Build     rebuild, then run
#   .\run.ps1 -Preview   don't start ProGuard: render every page and dialog of the UI to PNG against a stand-in
#                        engine (no real engine or administrator rights needed). Anything after -Preview goes to
#                        the tool, e.g.  .\run.ps1 -Preview -Mode standard   or   .\run.ps1 -Preview my\folder --size 1600x1000
#                        Images land in artifacts\preview\<admin|standard>\ unless a folder is given.
#                        .\run.ps1 -Preview --help lists the tool's options (including --serve and --probe).
param(
    [switch]$Build,
    [switch]$Preview,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$PreviewArgs
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$sln = Join-Path $root "ProGuard.sln"
$engineDir = Join-Path $root "src\ProGuard.Engine\bin\Debug\net8.0-windows\win-x64"
$enginePath = Join-Path $engineDir "ProGuard.Engine.exe"
$uiDir = Join-Path $root "src\ProGuard.UI\bin\Debug\net8.0-windows"
$uiPath = Join-Path $uiDir "ProGuard.UI.exe"
$previewProject = Join-Path $root "tools\ProGuard.Preview"
$previewPath = Join-Path $previewProject "bin\Debug\net8.0-windows\ProGuard.Preview.exe"

if ($Preview) {
    # Always build the tool: it renders the UI as it is now, and an up-to-date build costs a second.
    dotnet build $previewProject -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }

    # The tool resolves its default folder (artifacts\preview) from where it is run, so run it from here.
    # PowerShell turns -Mode into --mode for the tool; anything else is passed as written.
    $toolArgs = @($PreviewArgs | ForEach-Object { if ($_ -match '^-[A-Za-z]') { '-' + $_.ToLowerInvariant() } else { $_ } })
    Push-Location $root
    try {
        & $previewPath @toolArgs
        exit $LASTEXITCODE
    } finally {
        Pop-Location
    }
}

if ($Build -or -not (Test-Path $enginePath) -or -not (Test-Path $uiPath)) {
    Write-Host "Building..." -ForegroundColor Yellow
    dotnet build $sln -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

# Only an engine started from this folder speaks this UI's protocol; an older copy elsewhere does not.
# (An elevated engine hides its path from an unelevated shell: then all we can say is that one is running.)
$engines = @(Get-Process -Name ProGuard.Engine -ErrorAction SilentlyContinue)
$ours = $engines | Where-Object { $_.Path -eq $enginePath } | Select-Object -First 1
$unknown = $engines | Where-Object { -not $_.Path } | Select-Object -First 1
foreach ($other in ($engines | Where-Object { $_.Path -and $_.Path -ne $enginePath })) {
    Write-Host "Note: another ProGuard engine is running from $($other.Path) (PID $($other.Id)). This UI won't talk to it." -ForegroundColor DarkYellow
}

if ($ours) {
    Write-Host "Engine already running (PID $($ours.Id))." -ForegroundColor Green
} elseif ($unknown) {
    Write-Host "An engine is running (PID $($unknown.Id)) but Windows won't say where from: run this script elevated to check. Not starting a second one." -ForegroundColor DarkYellow
} else {
    $engine = Start-Process -FilePath $enginePath -WorkingDirectory $engineDir -WindowStyle Hidden -PassThru
    Write-Host "Engine started (PID $($engine.Id))." -ForegroundColor Green
}

$ui = Start-Process -FilePath $uiPath -WorkingDirectory $uiDir -PassThru
Write-Host "UI started (PID $($ui.Id))." -ForegroundColor Green
$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $elevated) {
    Write-Host "Run this script from an elevated terminal to let ProGuard manage services and system processes." -ForegroundColor DarkGray
}
