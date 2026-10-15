using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using UsageWidget;

if (args.Contains("app-server")) { await Fixture(); return; }
var root = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "aiusage-process-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
    checks++; Console.WriteLine("PASS: " + label);
}
CodexClient Client(string name, string mode = "normal") => new(Path.Combine(root, name), () =>
{
    var info = new ProcessStartInfo(Environment.ProcessPath!);
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    info.Environment["AI_USAGE_PROCESS_FIXTURE"] = mode;
    return info;
});
async Task Stopped(int pid)
{
    for (var attempt = 0; attempt < 100; attempt++)
    {
        try { using var process = Process.GetProcessById(pid); if (process.HasExited) return; }
        catch (ArgumentException) { return; }
        await Task.Delay(10);
    }
    throw new InvalidOperationException("Owned reader survived idle cleanup: " + pid);
}

using (var client = Client("refresh"))
{
    var auth = Path.Combine(root, "refresh", "auth.json");
    Directory.CreateDirectory(Path.GetDirectoryName(auth)!);
    await File.WriteAllTextAsync(auth, "fixture-auth-must-remain");
    for (var iteration = 0; iteration < 12; iteration++)
    {
        var account = await client.ReadAccountAsync(default);
        var pid = client.ReaderProcessId;
        var usage = await client.ReadAsync(default);
        Check(account.Status == "connected" && usage.Windows[0].RemainingPercent == 77
            && pid == client.ReaderProcessId, "A polling batch shares one reader and preserves its results");
        client.ReleaseIdleResources();
        Check(client.ReaderProcessId is null, "Idle reader reference is released");
        await Stopped(pid!.Value);
    }
    Check(await File.ReadAllTextAsync(auth) == "fixture-auth-must-remain", "Reader cleanup preserves the CLI authentication file");
}

using (var client = Client("pending", "slow-usage"))
{
    await client.ReadAccountAsync(default);
    var pending = client.ReadAsync(default);
    await Task.Delay(40);
    var pid = client.ReaderProcessId;
    client.ReleaseIdleResources();
    Check(pid is not null && client.ReaderProcessId == pid && !pending.IsCompleted, "Idle cleanup preserves a request in progress");
    Check((await pending).Windows[0].RemainingPercent == 77, "The preserved request completes normally");
    client.ReleaseIdleResources(); await Stopped(pid!.Value);
}

using (var client = Client("login", "early-login"))
{
    var url = await client.BeginLoginAsync(default);
    var pid = client.ReaderProcessId;
    client.ReleaseIdleResources();
    Check(url == "https://auth.openai.com/fixture" && client.ReaderProcessId == pid, "A login keeps its reader even if completion arrives before its response");
    await client.WaitForLoginAsync(default);
    client.ReleaseIdleResources(); await Stopped(pid!.Value);
    Check((await client.ReadAccountAsync(default)).Status == "connected", "Account queries reconnect after login reader cleanup");
    client.ReleaseIdleResources();
}

using (var client = Client("cancel", "pending-login"))
{
    await client.BeginLoginAsync(default);
    var pid = client.ReaderProcessId;
    using var cancel = new CancellationTokenSource(60);
    try { await client.WaitForLoginAsync(cancel.Token); throw new InvalidOperationException("Login unexpectedly completed"); }
    catch (OperationCanceledException) { }
    await client.CancelLoginAsync(default);
    client.ReleaseIdleResources(); await Stopped(pid!.Value);
    Check(client.ReaderProcessId is null, "Cancelled login releases its reader");
}

using (var client = Client("failure", "failed-usage"))
{
    await client.ReadAccountAsync(default);
    var pid = client.ReaderProcessId;
    try { await client.ReadAsync(default); throw new InvalidOperationException("Expected an RPC error"); }
    catch (IOException) { }
    client.ReleaseIdleResources(); await Stopped(pid!.Value);
    Check(client.ReaderProcessId is null, "Failed queries also release their reader");
}

await File.WriteAllTextAsync(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{checks} portable production-process checks passed.");

static async Task Fixture()
{
    var mode = Environment.GetEnvironmentVariable("AI_USAGE_PROCESS_FIXTURE");
    async Task Emit(object message) { await Console.Out.WriteLineAsync(JsonSerializer.Serialize(message)); await Console.Out.FlushAsync(); }
    while (await Console.In.ReadLineAsync() is { } line)
    {
        using var json = JsonDocument.Parse(line);
        var message = json.RootElement;
        if (!message.TryGetProperty("id", out var id)) continue;
        object result = new { };
        switch (message.GetProperty("method").GetString())
        {
            case "account/read": result = new { account = new { type = "chatgpt", email = "fixture@example.test", planType = "plus" } }; break;
            case "account/rateLimits/read":
                if (mode == "slow-usage") await Task.Delay(250);
                if (mode == "failed-usage") { await Emit(new { id, error = new { message = "fixture error" } }); continue; }
                result = new { rateLimits = new { primary = new { usedPercent = 23, windowDurationMins = 300 } } }; break;
            case "account/login/start":
                if (mode != "pending-login") await Emit(new { method = "account/login/completed", @params = new { loginId = "fixture-login", success = true } });
                result = new { loginId = "fixture-login", authUrl = "https://auth.openai.com/fixture" }; break;
            case "initialize": case "account/login/cancel": case "account/logout": break;
            default: throw new InvalidOperationException("Unexpected model or tool request");
        }
        await Emit(new { id, result });
    }
}
