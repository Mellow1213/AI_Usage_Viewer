param([switch]$KeepInstallation)
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path $PSScriptRoot ('artifacts\update-e2e\' + [Guid]::NewGuid().ToString('N'))
$feedDirectory = Join-Path $testRoot 'feed'
$installDirectory = Join-Path $testRoot 'installed'
$profileDirectory = Join-Path $testRoot 'profile'
$null = New-Item -ItemType Directory -Path $testRoot,$profileDirectory -Force
$sentinel = Join-Path $profileDirectory 'connections.json'
[IO.File]::WriteAllText($sentinel, '{"claudeEnabled":true,"fixture":"preserve-me"}')
$sentinelHash = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
$passed = [Collections.Generic.List[string]]::new()
function Assert-UpdateFixture([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
    $passed.Add($message)
    Write-Host "PASS: $message"
}
function Invoke-UpdateFixture([string]$mode, [string]$source) {
    $info = [Diagnostics.ProcessStartInfo]::new((Join-Path $installDirectory 'current\UsageWidget.Verification.exe'))
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WorkingDirectory = $testRoot
    foreach ($argument in @('--update-e2e', $mode, $profileDirectory, $source)) { $info.ArgumentList.Add($argument) }
    $fixtureProcess = [Diagnostics.Process]::Start($info)
    if (-not $fixtureProcess.WaitForExit(30000)) { $fixtureProcess.Kill(); throw "Packaged fixture $mode timed out." }
    if ($fixtureProcess.ExitCode -ne 0) { throw "Packaged fixture $mode failed. Inspect $profileDirectory\failure.txt" }
    $fixtureProcess.Dispose()
}
Push-Location $PSScriptRoot
try {
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Velopack restore failed.' }
    foreach ($version in @('1.1.0', '1.1.1')) {
        $publishDirectory = Join-Path $testRoot $version
        & dotnet publish .\WidgetVerification\UsageWidget.Verification.csproj -c Release -r win-x64 --self-contained true -o $publishDirectory "-p:Version=$version" -p:OutputType=WinExe
        if ($LASTEXITCODE -ne 0) { throw 'Fixture publish failed.' }
        & dotnet vpk pack --packId Mellow.AIUsageViewer.UpdateVerification --packTitle 'AI Usage Update Verification' --packVersion $version --packDir $publishDirectory --mainExe UsageWidget.Verification.exe --runtime win-x64 --icon .\UsageWidget\Assets\usage-widget.ico --shortcuts None --delta None --outputDir $feedDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Fixture packaging failed.' }
        if ($version -eq '1.1.0') {
            $initialInstaller = Join-Path $testRoot 'Initial-Setup.exe'
            Copy-Item -LiteralPath (Get-ChildItem -LiteralPath $feedDirectory -Filter '*-Setup.exe' | Select-Object -First 1).FullName -Destination $initialInstaller
        }
    }
    $setup = Start-Process -FilePath $initialInstaller -ArgumentList @('--silent', '--installto', ('"' + $installDirectory + '"')) -WindowStyle Hidden -PassThru -Wait
    Assert-UpdateFixture ($setup.ExitCode -eq 0) 'A real Velopack installer installs the isolated fixture'
    $corruptFeed = Join-Path $testRoot 'corrupt-feed'
    $null = New-Item -ItemType Directory -Path $corruptFeed -Force
    Copy-Item -LiteralPath (Join-Path $feedDirectory 'releases.win.json') -Destination $corruptFeed
    $latestPackage = Get-ChildItem -LiteralPath $feedDirectory -Filter '*1.1.1*full.nupkg' | Select-Object -First 1
    $corruptPackage = Join-Path $corruptFeed $latestPackage.Name
    Copy-Item -LiteralPath $latestPackage.FullName -Destination $corruptPackage
    $corruptStream = [IO.File]::Open($corruptPackage, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite)
    try {
        $corruptStream.Position = 4096
        $originalByte = $corruptStream.ReadByte()
        $corruptStream.Position = 4096
        $corruptStream.WriteByte([byte]($originalByte -bxor 1))
    } finally { $corruptStream.Dispose() }
    Invoke-UpdateFixture 'corrupt' $corruptFeed
    Assert-UpdateFixture (Test-Path -LiteralPath (Join-Path $profileDirectory 'corrupt-rejected.json')) 'A real corrupted package is rejected without enabling apply'
    Invoke-UpdateFixture 'download' $feedDirectory
    $downloaded = Get-Content -LiteralPath (Join-Path $profileDirectory 'downloaded.json') -Raw | ConvertFrom-Json
    Assert-UpdateFixture ($downloaded.currentVersion -eq '1.1.0' -and $downloaded.latestVersion -eq '1.1.1' -and $downloaded.canApply) 'A valid package downloads and verifies without changing the running version'
    Assert-UpdateFixture (-not (Test-Path -LiteralPath (Join-Path $profileDirectory 'applied.json'))) 'Downloaded updates are not automatically applied'
    Invoke-UpdateFixture 'apply' $feedDirectory
    $updateWatch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath (Join-Path $profileDirectory 'applied.json'))) {
        if ($updateWatch.Elapsed.TotalSeconds -gt 30) { throw 'Updated fixture did not restart. Inspect Velopack logs.' }
        Start-Sleep -Milliseconds 200
    }
    $applied = Get-Content -LiteralPath (Join-Path $profileDirectory 'applied.json') -Raw | ConvertFrom-Json
    Assert-UpdateFixture ($applied.currentVersion -eq '1.1.1') 'The real updater replaces version 1.1.0 and restarts version 1.1.1'
    Assert-UpdateFixture (Test-Path -LiteralPath (Join-Path $profileDirectory 'pending-restored.json')) 'A prepared update survives process exit until explicit application'
    Assert-UpdateFixture ((Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -eq $sentinelHash) 'Account/settings files outside the installation are preserved byte-for-byte'
    @{ success = $true; passed = @($passed); profile = $profileDirectory } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $testRoot 'results.json') -Encoding utf8
    Write-Host "Update E2E results: $testRoot\results.json"
} finally {
    if (-not $KeepInstallation -and (Test-Path -LiteralPath (Join-Path $installDirectory 'Update.exe'))) {
        $uninstall = Start-Process -FilePath (Join-Path $installDirectory 'Update.exe') -ArgumentList @('uninstall', '--silent') -WindowStyle Hidden -PassThru -Wait
        if ($uninstall.ExitCode -ne 0) { Write-Warning 'Could not remove the isolated verification installation.' }
    }
    Pop-Location
}
