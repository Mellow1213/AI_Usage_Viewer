param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-beta\.[1-9]\d*)?$')][string]$Version,
    [switch]$ResumeDraft
)
$ErrorActionPreference = 'Stop'
$repository = 'Mellow1213/AI_Usage_Viewer'
$tag = "v$Version"
$isPrerelease = $Version.Contains('-')
$packageDirectory = Join-Path $PSScriptRoot "artifacts\releases\$Version"
$publishDirectory = Join-Path $PSScriptRoot "artifacts\publish\$Version"
$notes = Join-Path $PSScriptRoot "docs\releases\$Version.md"
function Invoke-ReleaseCommand([string]$command, [string[]]$arguments) {
    $result = & $command @arguments
    if ($LASTEXITCODE -ne 0) { throw "$command $($arguments[0]) failed (exit $LASTEXITCODE)." }
    return $result
}
Push-Location $PSScriptRoot
try {
    # Release/ is legacy generated output. Preserve its unrelated local changes.
    $dirty = @(Invoke-ReleaseCommand git @('status', '--porcelain', '--untracked-files=all', '--', '.', ':(exclude)Release'))
    if ($dirty.Count -gt 0) { throw 'Commit source, tests, release notes and guides before publishing. Generated Release/ changes may remain local.' }
    $commit = (Invoke-ReleaseCommand git @('rev-parse', 'HEAD')).Trim()
    $origin = (Invoke-ReleaseCommand git @('remote', 'get-url', 'origin')).Trim()
    if ($origin -notmatch '^https://github\.com/Mellow1213/AI_Usage_Viewer(?:\.git)?/?$|^git@github\.com:Mellow1213/AI_Usage_Viewer(?:\.git)?$') {
        throw 'origin must point to Mellow1213/AI_Usage_Viewer.'
    }
    $remoteHead = (Invoke-ReleaseCommand git @('ls-remote', 'origin', 'refs/heads/main')) -split '\s+'
    if ($remoteHead[0] -ne $commit) { throw 'Push this source commit to origin/main before publishing.' }
    $runs = (Invoke-ReleaseCommand gh @('run', 'list', '--repo', $repository, '--workflow', 'desktop-verification.yml', '--commit', $commit,
        '--status', 'success', '--limit', '1', '--json', 'databaseId,headSha')) | ConvertFrom-Json
    if (@($runs).Count -eq 0 -or $runs[0].headSha -ne $commit) { throw 'Desktop verification must pass for this exact source commit before publishing.' }
    $verificationRun = [string]$runs[0].databaseId
    [xml]$project = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'UsageWidget\UsageWidget.csproj') -Raw
    if ([string]$project.Project.PropertyGroup.Version -ne $Version) { throw 'Set UsageWidget.csproj Version to the release version and commit it first.' }
    if (-not (Test-Path -LiteralPath $notes)) { throw "Missing release notes: docs/releases/$Version.md" }
    $repo = (Invoke-ReleaseCommand gh @('repo', 'view', $repository, '--json', 'isPrivate')) | ConvertFrom-Json
    if ($repo.isPrivate) { throw 'The update repository must be public so installed apps need no GitHub token.' }
    $releases = (Invoke-ReleaseCommand gh @('release', 'list', '--repo', $repository, '--limit', '1000', '--json', 'tagName,isDraft')) | ConvertFrom-Json
    $existing = @($releases | Where-Object tagName -eq $tag)
    if ($existing.Count -gt 0 -and -not $existing[0].isDraft) { throw "$tag is already published. Never overwrite it; increase the version." }
    if ($existing.Count -gt 0 -and -not $ResumeDraft) { throw 'An unfinished draft exists. Inspect it, then retry with -ResumeDraft.' }
    $versionCore = [version]($Version.Split('-')[0])
    $betaNumber = if ($isPrerelease) { [int]($Version.Split('.')[-1]) } else { [int]::MaxValue }
    foreach ($release in @($releases | Where-Object { -not $_.isDraft })) {
        if ($release.tagName -match '^v(\d+\.\d+\.\d+)(?:-beta\.([1-9]\d*))?$') {
            $publishedCore = [version]$Matches[1]
            $publishedBeta = if ($Matches[2]) { [int]$Matches[2] } else { [int]::MaxValue }
            if ($publishedCore -gt $versionCore -or ($publishedCore -eq $versionCore -and $publishedBeta -ge $betaNumber)) {
                throw 'The version must be greater than every already published stable or beta version.'
            }
        }
    }
    $latest = (Invoke-ReleaseCommand gh @('release', 'view', '--repo', $repository, '--json', 'tagName')) | ConvertFrom-Json
    if ($latest.tagName -match '^v(\d+\.\d+\.\d+)$' -and [version]($Version.Split('-')[0]) -le [version]$Matches[1]) {
        throw 'The release version core must be greater than the current latest stable release.'
    }
    $remoteTag = @(Invoke-ReleaseCommand git @('ls-remote', 'origin', "refs/tags/$tag"))
    if ($remoteTag.Count -gt 0) {
        $tagCommit = (Invoke-ReleaseCommand gh @('api', "repos/$repository/commits/$tag", '--jq', '.sha')).Trim()
        if ($tagCommit -ne $commit) { throw "$tag points to a different source commit." }
    }
    if (-not (Test-Path -LiteralPath $packageDirectory)) {
        & .\package-widget.ps1 -Version $Version -OutputDirectory $packageDirectory -PublishDirectory $publishDirectory -ReleaseNotes $notes
        @{ version = $Version; sourceCommit = $commit; repository = "https://github.com/$repository"; runtime = 'win-x64' } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageDirectory 'release-build.json') -Encoding utf8
    } elseif (-not $ResumeDraft) { throw 'Release output already exists. Use a new version, or inspect and resume this exact build with -ResumeDraft.' }
    $provenance = Get-Content -LiteralPath (Join-Path $packageDirectory 'release-build.json') -Raw | ConvertFrom-Json
    if ($provenance.sourceCommit -ne $commit -or $provenance.version -ne $Version) { throw 'Existing packages were built from a different source/version.' }
    $macFiles = foreach ($platform in @(@{ Runner='macos-14'; Arch='arm64' }, @{ Runner='macos-15-intel'; Arch='x86_64' })) {
        $macDirectory = Join-Path $packageDirectory ('macos-' + $platform.Arch)
        if (-not (Test-Path -LiteralPath $macDirectory)) {
            $null = Invoke-ReleaseCommand gh @('run', 'download', $verificationRun, '--repo', $repository,
                '--name', ('macos-' + $platform.Runner), '--dir', $macDirectory)
        }
        $macBuild = Get-Content -LiteralPath (Join-Path $macDirectory ('build-' + $platform.Arch + '.json')) -Raw | ConvertFrom-Json
        if ($macBuild.sourceCommit -ne $commit -or $macBuild.version -ne $Version -or $macBuild.architecture -ne $platform.Arch) {
            throw 'Mac artifacts must come from the same verified source commit, version and CPU architecture.'
        }
        $macName = "AIUsageWidget-$Version-macos-$($platform.Arch).zip"
        Copy-Item -LiteralPath (Join-Path $macDirectory $macName) -Destination (Join-Path $packageDirectory $macName) -Force
        Join-Path $packageDirectory $macName
    }
    $provenance | Add-Member -NotePropertyName macosVerificationRunId -NotePropertyValue $verificationRun -Force
    $provenance | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageDirectory 'release-build.json') -Encoding utf8
    $feed = Get-Content -LiteralPath (Join-Path $packageDirectory 'releases.win.json') -Raw | ConvertFrom-Json
    if (@($feed.Assets).Count -ne 1 -or $feed.Assets[0].PackageId -ne 'Mellow.AIUsageViewer' -or
        $feed.Assets[0].Version -ne $Version -or $feed.Assets[0].Type -ne 'Full') { throw 'Unexpected package identity, version or feed contents.' }
    $asset = $feed.Assets[0]
    if ([IO.Path]::GetFileName($asset.FileName) -ne $asset.FileName) { throw 'Unsafe package filename.' }
    $fullPackage = Join-Path $packageDirectory $asset.FileName
    if ((Get-Item -LiteralPath $fullPackage).Length -ne $asset.Size -or
        (Get-FileHash -LiteralPath $fullPackage -Algorithm SHA256).Hash -ne $asset.SHA256) { throw 'Package size/hash does not match the update feed.' }
    $files = @($asset.FileName, 'Mellow.AIUsageViewer-win-Setup.exe', 'Mellow.AIUsageViewer-win-Portable.zip',
        'releases.win.json', 'RELEASES', 'release-build.json') | ForEach-Object { Join-Path $packageDirectory $_ }
    $files += $macFiles
    $checksums = foreach ($file in $files) {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($file)
    }
    $checksumFile = Join-Path $packageDirectory 'SHA256SUMS'
    $checksums | Set-Content -LiteralPath $checksumFile -Encoding ascii
    $files += $checksumFile
    $changedDuringBuild = @(Invoke-ReleaseCommand git @('status', '--porcelain', '--untracked-files=all', '--', '.', ':(exclude)Release'))
    if ($changedDuringBuild.Count -gt 0 -or (Invoke-ReleaseCommand git @('rev-parse', 'HEAD')).Trim() -ne $commit) {
        throw 'Source changed while packaging. Do not publish packages from an uncommitted or different source.'
    }
    if ($existing.Count -eq 0) {
        $null = Invoke-ReleaseCommand gh (@('release', 'create', $tag, '--repo', $repository, '--target', $commit,
            '--draft', '--title', "AI Usage Viewer $tag", '--notes-file', $notes) + $files)
    } else {
        $draft = (Invoke-ReleaseCommand gh @('release', 'view', $tag, '--repo', $repository, '--json', 'targetCommitish,isDraft')) | ConvertFrom-Json
        if (-not $draft.isDraft -or $draft.targetCommitish -ne $commit) { throw 'Only a draft for this exact source commit can be resumed.' }
        $null = Invoke-ReleaseCommand gh (@('release', 'upload', $tag, '--repo', $repository, '--clobber') + $files)
    }
    # Keep the release invisible until GitHub confirms every uploaded size and SHA256.
    $uploaded = (Invoke-ReleaseCommand gh @('release', 'view', $tag, '--repo', $repository, '--json', 'assets')) | ConvertFrom-Json
    foreach ($file in $files) {
        $remote = @($uploaded.assets | Where-Object name -eq ([IO.Path]::GetFileName($file)))
        $digest = 'sha256:' + (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($remote.Count -ne 1 -or $remote[0].size -ne (Get-Item -LiteralPath $file).Length -or $remote[0].digest -ne $digest) {
            throw "Uploaded asset did not verify: $file. Release remains a draft."
        }
    }
    $publishFlags = if ($isPrerelease) { @('--prerelease', '--latest=false') } else { @('--prerelease=false', '--latest') }
    $null = Invoke-ReleaseCommand gh (@('release', 'edit', $tag, '--repo', $repository, '--draft=false') + $publishFlags)
    $published = (Invoke-ReleaseCommand gh @('release', 'view', $tag, '--repo', $repository, '--json', 'tagName,isDraft,isPrerelease,url')) | ConvertFrom-Json
    if ($published.isDraft -or $published.tagName -ne $tag -or $published.isPrerelease -ne $isPrerelease) { throw 'The release visibility/channel does not match the requested version.' }
    $latestAfter = (Invoke-ReleaseCommand gh @('release', 'view', '--repo', $repository, '--json', 'tagName')) | ConvertFrom-Json
    $expectedLatest = if ($isPrerelease) { $latest.tagName } else { $tag }
    if ($latestAfter.tagName -ne $expectedLatest) { throw 'The stable Latest release changed unexpectedly.' }
    Write-Host "Published and verified: $($published.url)"
    Write-Host 'Complete the anonymous update check/download with verify-widget-release.ps1 before reporting completion.'
} finally { Pop-Location }
