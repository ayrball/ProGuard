$root = $PSScriptRoot
$engineDir = Join-Path $root "src\ProGuard.Engine\bin\Debug\net8.0-windows\win-x64"
$enginePath = Join-Path $engineDir "ProGuard.Engine.exe"

$uiDir = Join-Path $root "src\ProGuard.UI\bin\Debug\net8.0-windows"
$uiPath = Join-Path $uiDir "ProGuard.UI.exe"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "       Launching ProGuard Platform       " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# 1. Start Engine Service (if not already running)
$engineProc = Get-Process -Name ProGuard.Engine -ErrorAction SilentlyContinue
if (-not $engineProc) {
    Write-Host "[*] Starting ProGuard Engine background service..." -ForegroundColor Yellow
    $engine = Start-Process -FilePath $enginePath -WorkingDirectory $engineDir -WindowStyle Hidden -PassThru
    Write-Host "[+] ProGuard Engine active (PID: $($engine.Id))" -ForegroundColor Green
} else {
    Write-Host "[+] ProGuard Engine is already running (PID: $($engineProc.Id))" -ForegroundColor Green
}

# 2. Start ProGuard UI
$uiProc = Get-Process -Name ProGuard.UI -ErrorAction SilentlyContinue
if (-not $uiProc) {
    Write-Host "[*] Launching ProGuard Fluent GUI..." -ForegroundColor Yellow
    $ui = Start-Process -FilePath $uiPath -WorkingDirectory $uiDir -PassThru
    Write-Host "[+] ProGuard UI launched successfully (PID: $($ui.Id))" -ForegroundColor Green
} else {
    Write-Host "[+] ProGuard UI is already open (PID: $($uiProc.Id))" -ForegroundColor Green
}

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "ProGuard is now active and monitoring your system!" -ForegroundColor Green
