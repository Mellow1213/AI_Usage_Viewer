param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\windows-release-checks'))
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$fixtureRoot = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $fixtureRoot 'artifacts\releases\1.2.7'
$fixtureCommit = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
$fixtureVersion = '1.2.7'
$ciCommit = $fixtureCommit
$fixtureState = @{ Published = $false; UploadedFiles = @() }
$badRemoteDigest = $false
$passed = [Collections.Generic.List[string]]::new()
function Assert-ReleaseCheck([bool]$condition, [string]$message) {
    if (-not $condition) { throw "FAIL: $message" }
    $passed.Add($message); Write-Host "PASS: $message"
}
function Assert-ReleaseRejected([string]$reason) {
    $failure = $null
    try { & (Join-Path $fixtureRoot 'publish-widget.ps1') -Version $fixtureVersion -ResumeDraft }
    catch { $failure = $_.Exception.Message }
    Assert-ReleaseCheck ($failure -like "*$reason*") "Publisher rejects $reason"
}
# Only the copied scripts run. Every external command is mocked: no GitHub mutation,
# network access, real package build or installed application access is permitted.
function git {
    $global:LASTEXITCODE = 0
    switch ($args[0]) {
        'status' { return }
        'rev-parse' { return $fixtureCommit }
        'remote' { return 'https://github.com/Mellow1213/AI_Usage_Viewer.git' }
        'ls-remote' { if ($args[2] -eq 'refs/heads/main') { return "$fixtureCommit`trefs/heads/main" }; return }
        default { throw "Unexpected git command: $args" }
    }
}
function gh {
    $global:LASTEXITCODE = 0
    $command = "$($args[0]) $($args[1])"
    switch ($command) {
        'run list' { return (@(@{databaseId=123;headSha=$ciCommit}) | ConvertTo-Json -AsArray) }
        'repo view' { return '{"isPrivate":false}' }
        'release list' { return '[{"tagName":"v1.2.7","isDraft":true},{"tagName":"v1.2.6","isDraft":false}]' }
        'release upload' {
            $fixtureState.UploadedFiles = @($args | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
            return
        }
        'release edit' { $fixtureState.Published = $true; return }
        'release view' {
            switch ($args[-1]) {
                'tagName' { return ( @{tagName= $(if ($fixtureState.Published) {'v1.2.7'} else {'v1.2.6'})} | ConvertTo-Json ) }
                'targetCommitish,isDraft' { return (@{targetCommitish=$fixtureCommit;isDraft=$true} | ConvertTo-Json) }
                'assets' {
                    $assets = @($fixtureState.UploadedFiles | ForEach-Object { @{
                        name = [IO.Path]::GetFileName($_); size = (Get-Item -LiteralPath $_).Length
                        digest = $(if ($badRemoteDigest) {'sha256:invalid'} else {'sha256:' + (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()})
                    } })
                    return (@{assets=$assets} | ConvertTo-Json -Depth 4)
                }
                'tagName,isDraft,isPrerelease,url' { return '{"tagName":"v1.2.7","isDraft":false,"isPrerelease":false,"url":"https://example.invalid/mock-release"}' }
            }
        }
    }
    throw "Unexpected gh command (including any Mac artifact download): $args"
}
function dotnet {
    $global:LASTEXITCODE = 0
    if ($args[-3] -ne '--github-release' -or $args[-2] -ne $fixtureVersion) { throw "Unexpected dotnet command: $args" }
    New-Item -ItemType Directory -Path $args[-1] -Force | Out-Null
    '{"success":true}' | Set-Content -LiteralPath (Join-Path $args[-1] 'results.json')
}
function Invoke-WebRequest { throw 'Unexpected network request in Windows release verification.' }

New-Item -ItemType Directory -Path $packageRoot, (Join-Path $fixtureRoot 'UsageWidget'), (Join-Path $fixtureRoot 'docs\releases') -Force | Out-Null
foreach ($name in @('publish-widget.ps1', 'verify-widget-release.ps1')) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot $name) -Destination (Join-Path $fixtureRoot $name)
}
'<Project><PropertyGroup><Version>1.2.7</Version></PropertyGroup></Project>' | Set-Content -LiteralPath (Join-Path $fixtureRoot 'UsageWidget\UsageWidget.csproj')
'Windows fixture release' | Set-Content -LiteralPath (Join-Path $fixtureRoot 'docs\releases\1.2.7.md')
@{version=$fixtureVersion;sourceCommit=$fixtureCommit;runtime='win-x64'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageRoot 'release-build.json')
$fullName = 'Mellow.AIUsageViewer-1.2.7-full.nupkg'
foreach ($name in @($fullName, 'Mellow.AIUsageViewer-win-Setup.exe', 'Mellow.AIUsageViewer-win-Portable.zip', 'RELEASES')) {
    "fixture $name" | Set-Content -LiteralPath (Join-Path $packageRoot $name)
}
$fullFile = Join-Path $packageRoot $fullName
@{Assets=@(@{PackageId='Mellow.AIUsageViewer';Version=$fixtureVersion;Type='Full';FileName=$fullName;
    Size=(Get-Item -LiteralPath $fullFile).Length;SHA256=(Get-FileHash -LiteralPath $fullFile -Algorithm SHA256).Hash})} |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $packageRoot 'releases.win.json')

& (Join-Path $fixtureRoot 'publish-widget.ps1') -Version $fixtureVersion -ResumeDraft
Assert-ReleaseCheck ($fixtureState.Published -and $fixtureState.UploadedFiles.Count -eq 7 -and -not ($fixtureState.UploadedFiles -match 'macos')) 'Windows-only assets can pass the complete mocked publication flow'
$provenance = Get-Content -LiteralPath (Join-Path $packageRoot 'release-build.json') -Raw | ConvertFrom-Json
Assert-ReleaseCheck ($provenance.desktopVerificationRunId -eq '123' -and $provenance.runtime -eq 'win-x64') 'Provenance records the Windows verification run and runtime'
Assert-ReleaseCheck (@(Get-Content -LiteralPath (Join-Path $packageRoot 'SHA256SUMS')).Count -eq 6) 'Checksums cover every Windows package, feed and provenance asset'
& (Join-Path $fixtureRoot 'verify-widget-release.ps1') -Version $fixtureVersion
Assert-ReleaseCheck (@(Get-ChildItem -LiteralPath (Join-Path $fixtureRoot 'artifacts\github-release-checks') -Filter results.json -Recurse).Count -eq 1) 'Public verification invokes the anonymous Windows checker without requesting Mac ZIPs'
$ciCommit = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
Assert-ReleaseRejected 'exact source commit'
$ciCommit = $fixtureCommit
$fixtureState.Published = $false
$badRemoteDigest = $true
Assert-ReleaseRejected 'Uploaded asset did not verify'
Assert-ReleaseCheck (-not $fixtureState.Published) 'Invalid uploaded digests keep the release unpublished'
$badRemoteDigest = $false
Add-Content -LiteralPath $fullFile -Value 'tampered'
Assert-ReleaseRejected 'Package size/hash does not match'
@{success=$true;passed=$passed} | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $fixtureRoot 'results.json')
Write-Host "Release pipeline checks: $fixtureRoot"
