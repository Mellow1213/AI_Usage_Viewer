param([switch]$RestoreUI)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
Push-Location (Join-Path $projectRoot 'WidgetUI')
try {
    if ($RestoreUI -or -not (Test-Path -LiteralPath 'node_modules')) {
        & npm.cmd ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'React dependency installation failed.' }
    }
    & npm.cmd run build
    if ($LASTEXITCODE -ne 0) { throw 'React UI build failed.' }
} finally { Pop-Location }
$publishDirectory = Join-Path $projectRoot 'Release'
& dotnet publish (Join-Path $projectRoot 'UsageWidget\UsageWidget.csproj') -c Release -r win-x64 --self-contained true -o $publishDirectory --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw 'WPF publish failed.' }
Write-Host ('Built: ' + (Join-Path $publishDirectory 'AIUsageWidget.exe'))
