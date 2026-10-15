# Runs on the test desktop: loads uia.ps1, then the given script (read as UTF-8).
param([string]$Script)
$ErrorActionPreference = 'Continue'
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8
. (Join-Path $PSScriptRoot 'uia.ps1')
. ([scriptblock]::Create((Get-Content -Raw -Encoding UTF8 $Script)))
