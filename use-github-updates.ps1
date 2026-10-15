param([string]$DataDirectory = (Join-Path $env:LOCALAPPDATA 'AIUsageWidget'))
$ErrorActionPreference = 'Stop'
# The static endpoint also works in the original 1.1.0 installed app.
$source = 'https://github.com/Mellow1213/AI_Usage_Viewer/releases/latest/download'
$configuration = Join-Path $DataDirectory 'update-source.json'
$null = New-Item -ItemType Directory -Path $DataDirectory -Force
if (Test-Path -LiteralPath $configuration) {
    Copy-Item -LiteralPath $configuration -Destination ($configuration + '.previous') -Force
}
$temporary = $configuration + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
@{ source = $source } | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding utf8
Move-Item -LiteralPath $temporary -Destination $configuration -Force
Write-Host 'GitHub Releases updates configured. Restart the app once to reload the source.'
