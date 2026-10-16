param([switch]$RestoreUI)
$ErrorActionPreference = 'Stop'
# Never replace Release/ or the installed application while building a development copy.
& (Join-Path $PSScriptRoot 'build-widget.ps1') -RestoreUI:$RestoreUI -OutputDirectory (Join-Path $PSScriptRoot 'artifacts\dev-build')
