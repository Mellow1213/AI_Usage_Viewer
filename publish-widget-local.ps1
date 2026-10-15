param(
    [string]$PackageDirectory = (Join-Path $PSScriptRoot 'artifacts\releases'),
    [switch]$Install,
    [switch]$Launch
)
$ErrorActionPreference = 'Stop'
$feedDirectory = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'AIUsageViewerUpdates'))
$installDirectory = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Mellow.AIUsageViewer'))
$dataDirectory = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'AIUsageWidget'))
foreach ($targetDirectory in @($feedDirectory, $installDirectory)) {
    if ($targetDirectory.Equals($dataDirectory, [StringComparison]::OrdinalIgnoreCase) -or
        $targetDirectory.StartsWith($dataDirectory + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $dataDirectory.StartsWith($targetDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Application installation/update files must never overlap the account data directory.'
    }
}
$feedFile = Join-Path $PackageDirectory 'releases.win.json'
if (-not (Test-Path -LiteralPath $feedFile)) { throw 'No Velopack release feed found. Run package-widget.ps1 first.' }
$feed = Get-Content -LiteralPath $feedFile -Raw | ConvertFrom-Json
if (-not $feed.Assets -or @($feed.Assets | Where-Object { $_.PackageId -ne 'Mellow.AIUsageViewer' }).Count -gt 0) {
    throw 'The release feed contains an unexpected application package.'
}
foreach ($packageFile in Get-ChildItem -LiteralPath $PackageDirectory -File -Filter '*.nupkg') {
    $existingPackage = Join-Path $feedDirectory $packageFile.Name
    if ((Test-Path -LiteralPath $existingPackage) -and (Get-FileHash -LiteralPath $existingPackage -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $packageFile.FullName -Algorithm SHA256).Hash) {
        throw "Published package $($packageFile.Name) has different contents. Use a new version number."
    }
}
$null = New-Item -ItemType Directory -Path $feedDirectory -Force
foreach ($packageFile in Get-ChildItem -LiteralPath $PackageDirectory -File) {
    if ($packageFile.Name -match '^Mellow\.AIUsageViewer.*\.(nupkg|exe)$') {
        Copy-Item -LiteralPath $packageFile.FullName -Destination (Join-Path $feedDirectory $packageFile.Name) -Force
    }
}
# Publish the feed last, so an app never observes metadata before its packages are copied.
$feedTemporary = Join-Path $feedDirectory ('releases.win.json.' + [Guid]::NewGuid().ToString('N') + '.tmp')
Copy-Item -LiteralPath $feedFile -Destination $feedTemporary
Move-Item -LiteralPath $feedTemporary -Destination (Join-Path $feedDirectory 'releases.win.json') -Force
$configuration = Join-Path $dataDirectory 'update-source.json'
if (-not (Test-Path -LiteralPath $configuration)) {
    $null = New-Item -ItemType Directory -Path $dataDirectory -Force
    @{ source = $feedDirectory } | ConvertTo-Json | Set-Content -LiteralPath $configuration -Encoding utf8
}
Write-Host "Published local update feed: $feedDirectory"
if ($Install) {
    $installer = Get-ChildItem -LiteralPath $feedDirectory -File -Filter '*-Setup.exe' | Select-Object -First 1
    if (-not $installer) { throw 'The Velopack installer is missing.' }
    $setup = Start-Process -FilePath $installer.FullName -ArgumentList @('--silent', '--installto', ('"' + $installDirectory + '"')) -WindowStyle Hidden -PassThru -Wait
    if ($setup.ExitCode -ne 0) { throw "Installation failed: exit code $($setup.ExitCode)." }
    Write-Host "Installed AI Usage Viewer: $installDirectory"
}
if ($Launch) {
    $stableLauncher = Join-Path $installDirectory 'Update.exe'
    if (-not (Test-Path -LiteralPath $stableLauncher)) { throw 'Install the application with -Install before launching.' }
    Start-Process -FilePath $stableLauncher -ArgumentList @('start', 'AIUsageWidget.exe') -WorkingDirectory $installDirectory -WindowStyle Hidden
}
