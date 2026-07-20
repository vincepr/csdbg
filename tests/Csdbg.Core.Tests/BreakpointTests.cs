using System.Text.Json;
using System.Text.Json.Nodes;
using Csdbg.Core.Dap;

namespace Csdbg.Core.Tests;

public sealed class BreakpointTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(1);
    private const string SourceFile = "/tmp/scripted-breakpoints.cs";

    [Fact]
    public async Task AddBreakpointAsync_WhenSynchronizationFails_RollsBackLocalBreakpoint()
    {
        var client = CreateInitializedClient();
        client.OnRequest = (request, _) => request.Command == "setBreakpoints"
            ? Task.FromException<JsonObject>(new IOException("scripted synchronization failure"))
            : Task.FromResult(ScriptedDapClient.Success(request.Command));
        await using var session = CreateSession(client);
        await session.EnsureStartedAsync().WaitAsync(TestTimeout);

        await Assert.ThrowsAsync<IOException>(
            () => session.AddBreakpointAsync(SourceFile, 42).WaitAsync(TestTimeout));

        Assert.Empty(GetStatusBreakpoints(session));
    }

    [Fact]
    public async Task RemoveBreakpointAsync_WhenSynchronizationFails_RestoresBreakpointWithSameId()
    {
        var client = CreateInitializedClient();
        client.OnRequest = (request, _) => Task.FromResult(
            request.Command == "setBreakpoints"
                ? SetBreakpointsSuccess((101, true, 47))
                : ScriptedDapClient.Success(request.Command));
        await using var session = CreateSession(client);
        await session.EnsureStartedAsync().WaitAsync(TestTimeout);
        var added = Assert.IsType<BreakpointInfo>(
            await session.AddBreakpointAsync(SourceFile, 42).WaitAsync(TestTimeout));

        client.OnRequest = (request, _) => request.Command == "setBreakpoints"
            ? Task.FromException<JsonObject>(new IOException("scripted synchronization failure"))
            : Task.FromResult(ScriptedDapClient.Success(request.Command));

        await Assert.ThrowsAsync<IOException>(
            () => session.RemoveBreakpointAsync(added.Id).WaitAsync(TestTimeout));

        var restored = Assert.Single(GetStatusBreakpoints(session));
        Assert.Equal(added.Id, restored["Id"]?.GetValue<string>());
        Assert.Equal(42, restored["RequestedLine"]?.GetValue<int>());
        Assert.Equal(47, restored["Line"]?.GetValue<int>());
        Assert.Equal(101, restored["AdapterId"]?.GetValue<int>());
    }

    [Fact]
    public async Task AddBreakpointAsync_WhenSetBreakpointsResponseIsUnsuccessful_Throws()
    {
        var client = CreateInitializedClient();
        client.OnRequest = (request, _) => Task.FromResult(
            request.Command == "setBreakpoints"
                ? Failure("setBreakpoints", "adapter rejected breakpoints")
                : ScriptedDapClient.Success(request.Command));
        await using var session = CreateSession(client);
        await session.EnsureStartedAsync().WaitAsync(TestTimeout);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.AddBreakpointAsync(SourceFile, 42).WaitAsync(TestTimeout));

        Assert.Equal("adapter rejected breakpoints", exception.Message);
        Assert.Equal(1, client.RequestCount("setBreakpoints"));
    }

    [Fact]
    public async Task AddBreakpointAsync_AfterAdapterResolvesLine_ResendsRequestedLine()
    {
        var client = CreateInitializedClient();
        client.OnRequest = (request, _) => Task.FromResult(
            request.Command == "setBreakpoints" && client.RequestCount("setBreakpoints") == 1
                ? SetBreakpointsSuccess((101, true, 47))
                : SetBreakpointsSuccess((101, true, 47), (102, true, 60)));
        await using var session = CreateSession(client);
        await session.EnsureStartedAsync().WaitAsync(TestTimeout);

        await session.AddBreakpointAsync(SourceFile, 42).WaitAsync(TestTimeout);
        await session.AddBreakpointAsync(SourceFile, 60).WaitAsync(TestTimeout);

        var requests = client.Requests
            .Where(request => request.Command == "setBreakpoints")
            .ToArray();
        Assert.Equal(2, requests.Length);
        var sentLines = requests[1].Arguments?["breakpoints"]?.AsArray()
            .Select(item => item?["line"]?.GetValue<int>())
            .ToArray();
        Assert.Equal(new int?[] { 42, 60 }, sentLines);
    }

    [Fact]
    public async Task GetStatus_AfterAdapterResolvesLine_ExposesRequestedAndResolvedLines()
    {
        var client = CreateInitializedClient();
        client.OnRequest = (request, _) => Task.FromResult(
            request.Command == "setBreakpoints"
                ? SetBreakpointsSuccess((101, true, 47))
                : ScriptedDapClient.Success(request.Command));
        await using var session = CreateSession(client);
        await session.EnsureStartedAsync().WaitAsync(TestTimeout);

        await session.AddBreakpointAsync(SourceFile, 42).WaitAsync(TestTimeout);

        var breakpoint = Assert.Single(GetStatusBreakpoints(session));
        Assert.Equal(42, breakpoint["RequestedLine"]?.GetValue<int>());
        Assert.Equal(47, breakpoint["Line"]?.GetValue<int>());
    }

    private static ScriptedDapClient CreateInitializedClient()
    {
        return new ScriptedDapClient
        {
            OnStart = client => client.EmitInitialized()
        };
    }

    private static DebugSession CreateSession(ScriptedDapClient client)
    {
        return new DebugSession(
            () => new BackendInfo { Path = "/fake/netcoredbg" },
            new ScriptedDapClientFactory(client));
    }

    private static JsonObject[] GetStatusBreakpoints(DebugSession session)
    {
        return JsonSerializer.SerializeToNode(session.GetStatus())!["breakpoints"]!
            .AsArray()
            .Select(item => item!.AsObject())
            .ToArray();
    }

    private static JsonObject SetBreakpointsSuccess(params (int Id, bool Verified, int Line)[] breakpoints)
    {
        return ScriptedDapClient.Success("setBreakpoints", new JsonObject
        {
            ["breakpoints"] = new JsonArray(breakpoints.Select(breakpoint =>
                (JsonNode)new JsonObject
                {
                    ["id"] = breakpoint.Id,
                    ["verified"] = breakpoint.Verified,
                    ["line"] = breakpoint.Line
                }).ToArray())
        });
    }

    private static JsonObject Failure(string command, string message)
    {
        return new JsonObject
        {
            ["type"] = "response",
            ["command"] = command,
            ["success"] = false,
            ["message"] = message
        };
    }
}
