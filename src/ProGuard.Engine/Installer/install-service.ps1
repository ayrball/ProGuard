$serviceName = "ProGuardEngine"
$exePath = Resolve-Path "..\bin\Release\net8.0-windows\win-x64\publish\ProGuard.Engine.exe"

if (-not (Test-Path $exePath)) {
    Write-Host "Please publish the project first."
    Exit 1
}

Write-Host "Installing $serviceName..."
sc.exe create $serviceName binPath= "$exePath" start= auto
sc.exe description $serviceName "ProGuard Engine Service for process management."
sc.exe start $serviceName
Write-Host "Done!"
