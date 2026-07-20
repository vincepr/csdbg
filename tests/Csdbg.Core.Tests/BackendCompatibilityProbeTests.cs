using System.Text.Json.Nodes;

namespace Csdbg.Core.Tests;

public sealed class BackendCompatibilityProbeTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(2);
    private const string BackendPath = "/fake/netcoredbg";

    [Fact]
    public async Task RunAsync_StopAtEntry_UsesExactTargetAndDisconnectsBeforeDisposal()
    {
        var client = new ScriptedDapClient();
        client.OnRequest = (request, _) =>
        {
            if (request.Command == "launch")
            {
                client.EmitInitialized();
            }
            else if (request.Command == "configurationDone")
            {
                client.EmitStopped(reason: "entry");
            }

            return Task.FromResult(ScriptedDapClient.Success(request.Command));
        };
        var factory = new ScriptedDapClientFactory(client);
        var probe = CreateProbe(factory);
        var target = new BackendProbeTarget(
            Path.Combine(Path.GetTempPath(), "csdbg-probe", "probe.dll"),
            Path.Combine(Path.GetTempPath(), "csdbg-probe", "working"),
            ["--mode", "compatibility probe"]);

        await probe.RunAsync(target).WaitAsync(TestTimeout);

        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(BackendPath, factory.LastPath);
        Assert.Equal(1, client.StartCount);
        Assert.Equal(1, client.DisposeCount);
        Assert.False(client.IsRunning);
        Assert.Equal(
            ["launch", "setExceptionBreakpoints", "configurationDone", "stackTrace", "disconnect"],
            client.Requests.Select(request => request.Command));

        var launch = client.Requests.Single(request => request.Command == "launch");
        Assert.Equal(Path.GetFullPath(target.Program), StringArgument(launch.Arguments, "program"));
        Assert.Equal(Path.GetFullPath(target.WorkingDirectory!), StringArgument(launch.Arguments, "cwd"));
        Assert.Equal(target.Arguments, StringArrayArgument(launch.Arguments, "args"));
        Assert.True(launch.Arguments!["stopAtEntry"]!.GetValue<bool>());
        Assert.False(launch.Arguments["justMyCode"]!.GetValue<bool>());

        var disconnect = client.Requests.Single(request => request.Command == "disconnect");
        Assert.False(disconnect.Arguments!["restart"]!.GetValue<bool>());
        Assert.True(disconnect.Arguments["terminateDebuggee"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RunAsync_LaunchDapFailure_DisconnectsAndDisposes()
    {
        var client = new ScriptedDapClient();
        client.OnRequest = (request, _) =>
        {
            if (request.Command == "launch")
            {
                client.EmitInitialized();
                return Task.FromResult(new JsonObject
                {
                    ["type"] = "response",
                    ["command"] = "launch",
                    ["success"] = false,
                    ["message"] = "scripted launch rejected"
                });
            }

            return Task.FromResult(ScriptedDapClient.Success(request.Command));
        };
        var factory = new ScriptedDapClientFactory(client);
        var probe = CreateProbe(factory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => probe.RunAsync(CreateTarget()).WaitAsync(TestTimeout));

        Assert.Equal("scripted launch rejected", exception.Message);
        Assert.Equal(
            ["launch", "setExceptionBreakpoints", "configurationDone", "disconnect"],
            client.Requests.Select(request => request.Command));
        Assert.Equal(1, client.RequestCount("disconnect"));
        Assert.Equal(1, client.DisposeCount);
        Assert.False(client.IsRunning);
    }

    [Fact]
    public async Task RunAsync_CancellationDuringLaunch_PropagatesAndCleansUp()
    {
        var launchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedDapClient();
        client.OnRequest = async (request, cancellationToken) =>
        {
            if (request.Command == "launch")
            {
                client.EmitInitialized();
                launchStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return ScriptedDapClient.Success(request.Command);
        };
        var factory = new ScriptedDapClientFactory(client);
        var probe = CreateProbe(factory);
        using var cancellation = new CancellationTokenSource();

        var runTask = probe.RunAsync(CreateTarget(), cancellation.Token);
        await launchStarted.Task.WaitAsync(TestTimeout);
        await client.WaitForRequestAsync("configurationDone", TestTimeout);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runTask.WaitAsync(TestTimeout));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, client.RequestCount("disconnect"));
        Assert.Equal(1, client.DisposeCount);
        Assert.False(client.IsRunning);
    }

    private static BackendCompatibilityProbe CreateProbe(ScriptedDapClientFactory factory) =>
        new(() => new BackendInfo { Path = BackendPath }, factory);

    private static BackendProbeTarget CreateTarget() =>
        new(
            Path.Combine(Path.GetTempPath(), "csdbg-probe", "probe.dll"),
            Path.Combine(Path.GetTempPath(), "csdbg-probe"),
            ["--probe"]);

    private static string StringArgument(JsonObject? arguments, string name) =>
        arguments![name]!.GetValue<string>();

    private static string[] StringArrayArgument(JsonObject? arguments, string name) =>
        arguments![name]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
}
