param([Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version)
$ErrorActionPreference = 'Stop'
$directory = Join-Path $PSScriptRoot ('artifacts\github-release-checks\' + $Version + '-' + [Guid]::NewGuid().ToString('N'))
Push-Location $PSScriptRoot
try {
    & dotnet run --project .\WidgetVerification\UsageWidget.Verification.csproj -c Release -- --github-release $Version $directory
    if ($LASTEXITCODE -ne 0) { throw "Anonymous release verification failed. Inspect $directory" }
    $publicBase = 'https://github.com/Mellow1213/AI_Usage_Viewer/releases/download/v' + $Version
    $checksums = (Invoke-WebRequest -Uri ($publicBase + '/SHA256SUMS')).Content
    if ($checksums -is [byte[]]) { $checksums = [Text.Encoding]::UTF8.GetString($checksums) }
    foreach ($architecture in @('arm64', 'x86_64')) {
        $name = "AIUsageWidget-$Version-macos-$architecture.zip"
        $download = Join-Path $directory $name
        Invoke-WebRequest -Uri ($publicBase + '/' + $name) -OutFile $download
        $expected = @($checksums -split '\r?\n' | Where-Object { $_ -match ('^[0-9a-f]{64}  ' + [regex]::Escape($name) + '$') })
        if ($expected.Count -ne 1 -or (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $expected[0].Substring(0,64)) {
            throw "Mac public package hash check failed: $name"
        }
        Write-Host "PASS: anonymous Mac $architecture download and SHA256"
    }
    Write-Host "Public release verification: $directory\results.json"
} finally { Pop-Location }
