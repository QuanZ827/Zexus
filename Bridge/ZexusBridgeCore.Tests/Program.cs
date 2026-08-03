using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Zexus.Bridge;

var failures = new List<string>();
var testCount = 0;
await Run("safety", () =>
{
    Assert(BridgeSafety.Check("System.IO.File.Delete(\"x\");").Count > 0, "dangerous code was not blocked");
    Assert(BridgeSafety.Check("return doc.Title;").Count == 0, "safe code was blocked");
    return Task.CompletedTask;
});
await Run("write signal", () =>
{
    Assert(BridgeWriteSignal.HasWriteSignal("using(var t=new Transaction(doc, \"x\")){t.Start();t.Commit();}"), "transaction not detected");
    Assert(!BridgeWriteSignal.HasWriteSignal("return doc.Title;"), "read-only code marked as write");
    return Task.CompletedTask;
});
await Run("loopback only", TestLoopbackOnly);
await Run("http bridge", TestHttpBridge);
Console.WriteLine($"Passed={testCount - failures.Count} Failed={failures.Count}");
foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

async Task Run(string name, Func<Task> test)
{
    testCount++;
    try { await test(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures.Add("FAIL " + name + ": " + ex.Message); }
}

async Task TestHttpBridge()
{
    var port = FreePort();
    var temp = Path.Combine(Path.GetTempPath(), "zexus-bridge-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    try
    {
        var options = new BridgeOptions { Port = port, PortFallbackCount = 1, TokenFile = Path.Combine(temp, "token.txt"), AuditLogPath = Path.Combine(temp, "audit.log"), WatchdogIntervalMs = 50 };
        using var bridge = new RevitBridgeServer(new FakeExecutor(), options);
        Assert(bridge.Start(), "server failed to start");
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{bridge.Port}") };
        Assert((await client.GetAsync("/health")).StatusCode == HttpStatusCode.OK, "health failed");
        Assert((await client.PostAsJsonAsync("/execute", new { code = "return doc.Title;" })).StatusCode == HttpStatusCode.Unauthorized, "missing token was accepted");
        Assert((await client.PostAsJsonAsync("/execute?token=" + bridge.Token, new { code = "return doc.Title;" })).StatusCode == HttpStatusCode.Unauthorized, "query-string token was accepted");
        client.DefaultRequestHeaders.Add("X-Zexus-Token", bridge.Token);
        Assert((await client.PostAsJsonAsync("/execute", new { code = "return doc.Title;" })).StatusCode == HttpStatusCode.Unauthorized, "non-Bearer token header was accepted");
        client.DefaultRequestHeaders.Remove("X-Zexus-Token");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bridge.Token);
        var blocked = await client.PostAsJsonAsync("/execute", new { code = "System.IO.File.Delete(\"x\");" });
        Assert(blocked.StatusCode == HttpStatusCode.BadRequest, "dangerous code bypassed the safety check");
        var bypass = await client.PostAsJsonAsync("/execute", new { code = "return doc.Title;", skipSafetyCheck = true });
        Assert(bypass.StatusCode == HttpStatusCode.BadRequest, "client supplied a safety bypass field");
        var create = await client.PostAsJsonAsync("/execute", new { description = "test", code = "return doc.Title;", isWriteOperation = false, timeoutSeconds = 5 });
        Assert(create.StatusCode == HttpStatusCode.Accepted, "execute was not accepted");
        var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("requestId").GetString();
        var result = await client.GetFromJsonAsync<JsonElement>("/requests/" + id);
        Assert(result.GetProperty("status").GetString() == "succeeded", "request did not succeed");
        Assert(File.Exists(options.TokenFile), "token file missing");
        bridge.Stop();
        Assert(!File.Exists(options.TokenFile), "token file not removed");
    }
    finally
    {
        Directory.Delete(temp, recursive: true);
    }
}

Task TestLoopbackOnly()
{
    var temp = Path.Combine(Path.GetTempPath(), "zexus-bridge-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(temp);
    var options = new BridgeOptions
    {
        BindHost = "localhost",
        Port = FreePort(),
        PortFallbackCount = 1,
        TokenFile = Path.Combine(temp, "token.txt"),
        AuditLogPath = Path.Combine(temp, "audit.log")
    };
    using (var bridge = new RevitBridgeServer(new FakeExecutor(), options))
    {
        Assert(!bridge.Start(), "bridge accepted a host other than 127.0.0.1");
    }
    Directory.Delete(temp, recursive: true);
    return Task.CompletedTask;
}

static int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
}
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

sealed class FakeExecutor : IBridgeExecutor
{
    public bool IsDocumentOpen => true;
    public string HealthDetail => "fake executor";
    public void Submit(BridgeRequestRecord record)
    {
        record.SetStatus(BridgeRequestStatus.Running);
        record.Complete(new BridgeCompletionData { Status = BridgeRequestStatus.Succeeded, Message = "ok", ResultJson = "{\"value\":\"fake\"}" });
    }
}
