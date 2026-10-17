@echo off
rem Closes ProGuard's window and stops its engine. Priorities it changed go back to normal as processes restart.
taskkill /im ProGuard.UI.exe /f >nul 2>&1
taskkill /im ProGuard.Engine.exe /f >nul 2>&1
echo ProGuard stopped.
ping -n 3 127.0.0.1 >nul
