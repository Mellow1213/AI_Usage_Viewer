param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')][string]$Version,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\releases'),
    [string]$PublishDirectory = (Join-Path $PSScriptRoot 'Release'),
    [string]$ReleaseNotes,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if (-not $SkipBuild) { & .\build-widget.ps1 -Version $Version -OutputDirectory $PublishDirectory }
    $publishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
    $packageDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    if ($packageDirectory -eq $publishDirectory -or $packageDirectory.StartsWith($publishDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Package output must be outside the Release application directory.'
    }
    $actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishDirectory 'AIUsageWidget.dll')).ProductVersion.Split('+')[0]
    if ($actualVersion -ne $Version) { throw "Release is version $actualVersion; expected $Version. Build with -Version $Version first." }
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Velopack tool restore failed.' }
    $packArguments = @('vpk', 'pack', '--packId', 'Mellow.AIUsageViewer', '--packTitle', 'AI Usage Viewer',
        '--packAuthors', 'Mellow1213', '--packVersion', $Version, '--packDir', $publishDirectory,
        '--mainExe', 'AIUsageWidget.exe', '--runtime', 'win-x64', '--icon', (Join-Path $PSScriptRoot 'UsageWidget\Assets\usage-widget.ico'),
        '--shortcuts', 'StartMenuRoot', '--outputDir', $packageDirectory)
    if (-not $ReleaseNotes) {
        $defaultNotes = Join-Path $PSScriptRoot "docs\releases\$Version.md"
        if (Test-Path -LiteralPath $defaultNotes) { $ReleaseNotes = $defaultNotes }
    }
    if ($ReleaseNotes) { $packArguments += @('--releaseNotes', [IO.Path]::GetFullPath($ReleaseNotes)) }
    & dotnet @packArguments
    if ($LASTEXITCODE -ne 0) { throw 'Velopack packaging failed.' }
    Write-Host "Packaged $Version in $packageDirectory"
} finally { Pop-Location }
