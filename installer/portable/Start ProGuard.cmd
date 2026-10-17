@echo off
rem Starts ProGuard: the engine (does the work, runs hidden) and then the window.
rem Right-click this file and choose "Run as administrator" to let ProGuard manage every process.
cd /d "%~dp0"
tasklist /fi "imagename eq ProGuard.Engine.exe" | find /i "ProGuard.Engine.exe" >nul
if errorlevel 1 (
    powershell -NoProfile -WindowStyle Hidden -Command "Start-Process -FilePath '%~dp0ProGuard.Engine.exe' -WorkingDirectory '%~dp0' -WindowStyle Hidden"
    ping -n 3 127.0.0.1 >nul
)
start "" "%~dp0ProGuard.UI.exe"
