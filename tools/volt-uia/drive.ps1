# drive.ps1 <script.ps1> : runs a UIA script on the hidden PgTest desktop with uia.ps1 loaded.
param([string]$Script)
$work = if ($env:PROGUARD_UIA_WORK) { $env:PROGUARD_UIA_WORK } else { Join-Path $PSScriptRoot 'work' }
& (Join-Path $work 'bin\rundesk.exe') PgTest powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'd.ps1') (Resolve-Path $Script)
