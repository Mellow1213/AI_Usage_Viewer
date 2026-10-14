using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace UsageWidget;

internal static class ClaudeUsageControl
{
    internal static async Task<ProviderUsage> ReadAsync(ProcessStartInfo info, CancellationToken token)
    {
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
        info.StandardOutputEncoding = System.Text.Encoding.UTF8;
        using var process = Process.Start(info) ?? throw new IOException("Claude usage reader did not start.");
        var errors = DrainErrorsAsync(process);
        try
        {
            await RequestAsync(process, "initialize", token);
            var usage = await RequestAsync(process, "get_usage", token);
            return UsageParser.ClaudeControl(usage);
        }
        finally
        {
            // Stop only this owned reader. There is no model turn, session
            // persistence, tool access, or modification to the official binary.
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            try { await errors.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
        }
    }

    private static async Task<JsonElement> RequestAsync(Process process, string subtype, CancellationToken token)
    {
        var id = Guid.NewGuid().ToString("N");
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { type = "control_request", request_id = id, request = new { subtype } }).AsMemory(), token);
        await process.StandardInput.FlushAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "control_response"
                || !root.TryGetProperty("response", out var response) || response.GetProperty("request_id").GetString() != id) continue;
            if (response.GetProperty("subtype").GetString() != "success" || !response.TryGetProperty("response", out var result))
                throw new IOException("Claude usage control is unavailable. Update Claude Code or sign in again.");
            return result.Clone();
        }
        throw new IOException("Claude usage reader closed.");
    }

    private static async Task DrainErrorsAsync(Process process)
    {
        // CLI diagnostics can contain account details. Never log or expose them.
        try { while (await process.StandardError.ReadLineAsync() is not null) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }
}
