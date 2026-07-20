using System.Text.Json;
using System.Text.Json.Nodes;

namespace Csdbg.Core.Tests;

public sealed class ExceptionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task Launch_UsesConfiguredExceptionFilters()
    {
        var client = new ScriptedDapClient();
        client.OnRequest = (request, _) =>
        {
            if (request.Command == "launch")
            {
                client.EmitInitialized();
            }

            return Task.FromResult(ScriptedDapClient.Success(request.Command));
        };
        await using var session = CreateSession(client);
        await session.SetExceptionBreakpointsAsync(["all", "user-unhandled", "all"]);

        await session.LaunchAsync("/tmp/scripted-debuggee.dll").WaitAsync(TestTimeout);

        var request = client.Requests.Single(item => item.Command == "setExceptionBreakpoints");
        Assert.Equal(
            ["all", "user-unhandled"],
            request.Arguments!["filters"]!.AsArray().Select(item => item!.GetValue<string>()));
    }

    [Fact]
    public async Task GetExceptionInfo_ForwardsAdapterDetails()
    {
        var client = new ScriptedDapClient
        {
            OnStart = dap =>
            {
                dap.EmitInitialized();
                dap.EmitStopped(reason: "exception");
            }
        };
        client.OnRequest = (request, _) => Task.FromResult(
            request.Command == "exceptionInfo"
                ? ScriptedDapClient.Success("exceptionInfo", new JsonObject
                {
                    ["exceptionId"] = "System.InvalidOperationException",
                    ["description"] = "Invalid quantity"
                })
                : ScriptedDapClient.Success(request.Command));
        await using var session = CreateSession(client);
        await session.EnsureStartedAsync().WaitAsync(TestTimeout);

        var result = await session.GetExceptionInfoAsync().WaitAsync(TestTimeout);
        var json = JsonSerializer.SerializeToNode(result)!.AsObject();

        Assert.Equal("System.InvalidOperationException", json["exception"]!["exceptionId"]!.GetValue<string>());
        Assert.Equal(1, client.Requests.Single(item => item.Command == "exceptionInfo").Arguments!["threadId"]!.GetValue<int>());
    }

    private static DebugSession CreateSession(ScriptedDapClient client) =>
        new(
            () => new BackendInfo { Path = "/fake/netcoredbg" },
            new ScriptedDapClientFactory(client));
}
