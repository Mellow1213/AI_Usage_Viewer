# Reads only Claude Code's documented statusLine payload. No credentials or conversations are saved.
$payload = [Console]::In.ReadToEnd()
$bridgeRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
try {
    $session = $payload | ConvertFrom-Json
    if ($null -ne $session.rate_limits) {
        $windows = @{}
        foreach ($key in @('five_hour', 'seven_day')) {
            $window = $session.rate_limits.$key
            if ($null -ne $window -and $null -ne $window.used_percentage) {
                $windows[$key] = @{ used_percentage = $window.used_percentage; resets_at = $window.resets_at }
            }
        }
        $snapshot = @{ updatedAt = [DateTimeOffset]::UtcNow.ToString('o'); rate_limits = $windows }
        $target = Join-Path $bridgeRoot 'claude-usage.json'
        $temporary = Join-Path $bridgeRoot ('claude-usage-' + [Guid]::NewGuid().ToString('N') + '.tmp')
        [IO.File]::WriteAllText($temporary, ($snapshot | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
        # File.Replace gives readers either the old or new complete snapshot.
        try {
            if ([IO.File]::Exists($target)) { [IO.File]::Replace($temporary, $target, $null) }
            else { [IO.File]::Move($temporary, $target) }
        } catch {
            if ([IO.File]::Exists($target) -and [IO.File]::Exists($temporary)) { [IO.File]::Replace($temporary, $target, $null) }
        } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
    }
} catch { }

# Retain a user's existing status line by passing the original payload to its original shell command.
try {
    $metadata = Get-Content -Raw -LiteralPath (Join-Path $bridgeRoot 'claude-bridge.json') | ConvertFrom-Json
    $original = $metadata.previousStatusLine.command
    if ($original) {
        $bashPath = $env:CLAUDE_CODE_GIT_BASH_PATH
        if (-not $bashPath) { $bashPath = 'C:\Program Files\Git\bin\bash.exe' }
        if (-not [IO.File]::Exists($bashPath)) {
            $found = Get-Command bash.exe -ErrorAction SilentlyContinue
            if ($found) { $bashPath = $found.Source }
        }
        if ([IO.File]::Exists($bashPath)) {
            # This is the user's already-configured shell command, encoded for ProcessStartInfo.
            $encoded = $original.Replace('\', '\\').Replace('"', '\"')
            $start = [Diagnostics.ProcessStartInfo]::new($bashPath, ('-c "' + $encoded + '"'))
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.RedirectStandardInput = $true
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            $process = [Diagnostics.Process]::Start($start)
            $outputTask = $process.StandardOutput.ReadToEndAsync()
            $errorTask = $process.StandardError.ReadToEndAsync()
            $process.StandardInput.Write($payload)
            $process.StandardInput.Close()
            if ($process.WaitForExit(3000)) { [Console]::Write($outputTask.GetAwaiter().GetResult()) }
            else { $process.Kill() }
            $process.Dispose()
        }
    } else {
        $five = $session.rate_limits.five_hour.used_percentage
        $week = $session.rate_limits.seven_day.used_percentage
        $parts = @()
        if ($null -ne $five) { $parts += ('5h ' + [Math]::Round(100 - $five) + '% left') }
        if ($null -ne $week) { $parts += ('7d ' + [Math]::Round(100 - $week) + '% left') }
        if ($parts.Count -gt 0) { [Console]::Write(($parts -join ' | ')) }
        else { [Console]::Write('AI Usage connected') }
    }
} catch { }
