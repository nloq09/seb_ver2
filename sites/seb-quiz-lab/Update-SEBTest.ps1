$ErrorActionPreference = 'Stop'
Write-Host 'SEB test updater version 2'
& (Join-Path $PSScriptRoot 'Update-SEBTest-v2.ps1')
