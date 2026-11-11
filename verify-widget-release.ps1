param([Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-beta\.[1-9]\d*)?$')][string]$Version)
$ErrorActionPreference = 'Stop'
$directory = Join-Path $PSScriptRoot ('artifacts\github-release-checks\' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
Push-Location $PSScriptRoot
try {
    & dotnet run --project .\WidgetVerification\UsageWidget.Verification.csproj -c Release -- --github-release $Version $directory
    if ($LASTEXITCODE -ne 0) { throw "Anonymous release verification failed. Inspect $directory" }
    Write-Host "Public release verification: $directory\results.json"
} finally { Pop-Location }
