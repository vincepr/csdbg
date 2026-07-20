using System.Text.Json.Nodes;
using Csdbg.Core.Dap;

namespace Csdbg.Core.Tests;

public sealed class DebugSessionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IncompleteCheckTimeout = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task ContinueAsync_WaitsForNewContinuedStoppedTransition()
    {
        var client = CreateStoppedClient();
        var factory = new ScriptedDapClientFactory(client);
        await using var session = CreateSession(factory);
        await session.EnsureStartedAsync().WaitAsync(TestTimeout);

        var continueTask = session.ContinueAsync(TestTimeout);
        await client.WaitForRequestAsync("continue", TestTimeout);

        await Assert.ThrowsAsync<TimeoutException>(
            () => continueTask.WaitAsync(IncompleteCheckTimeout));

        client.EmitContinued();
        client.EmitStopped(reason: "step");

        await continueTask.WaitAsync(TestTimeout);
        Assert.Equal("stopped", session.State);
        Assert.Equal(1, client.RequestCount("continue"));
    }

    [Theory]
    [InlineData("continue")]
    [InlineData("step_over")]
    [InlineData("step_into")]
    [InlineData("step_out")]
    public async Task ResumeCommand_WhenIdle_RejectsWithoutCreatingClient(string operation)
    {
        var client = new ScriptedDapClient();
        var factory = new ScriptedDapClientFactory(client);
        await using var session = CreateSession(factory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeResumeAsync(session, operation).WaitAsync(TestTimeout));

        Assert.Contains("active", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0, client.StartCount);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task PauseAsync_WhenStopped_RejectsWithoutSendingPause()
    {
        var client = CreateStoppedClient();
        var factory = new ScriptedDapClientFactory(client);
        await using var session = CreateSession(factory);
        await session.EnsureStartedAsync().WaitAsync(TestTimeout);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.PauseAsync(timeout: TestTimeout).WaitAsync(TestTimeout));

        Assert.Contains("running", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, client.RequestCount("pause"));
    }

    [Fact]
    public async Task LaunchAsync_StopAtEntry_PreservesSynchronousConfigurationDoneStop()
    {
        var client = new ScriptedDapClient();
        client.OnRequest = (request, _) =>
        {
            if (request.Command == "launch")
            {
                client.EmitInitialized();
            }

            if (request.Command == "configurationDone")
            {
                client.EmitStopped(reason: "entry");
            }

            return Task.FromResult(ScriptedDapClient.Success(request.Command));
        };
        var factory = new ScriptedDapClientFactory(client);
        await using var session = CreateSession(factory);
        using var cancellation = new CancellationTokenSource(TestTimeout);

        await session.LaunchAsync(
                "/tmp/scripted-debuggee.dll",
                stopAtEntry: true,
                cancellationToken: cancellation.Token)
            .WaitAsync(TestTimeout);

        Assert.Equal("stopped", session.State);
        Assert.Equal("entry", session.StopReason);
        Assert.Equal(1, client.RequestCount("configurationDone"));
    }

    [Fact]
    public async Task LaunchAsync_ConfiguresAfterInitializedBeforeDeferredLaunchResponse()
    {
        var launchResponse = new TaskCompletionSource<JsonObject>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedDapClient();
        client.OnRequest = async (request, cancellationToken) =>
        {
            if (request.Command == "launch")
            {
                client.EmitInitialized();
                return await launchResponse.Task.WaitAsync(cancellationToken);
            }

            if (request.Command == "configurationDone")
            {
                launchResponse.TrySetResult(ScriptedDapClient.Success("launch"));
            }

            return ScriptedDapClient.Success(request.Command);
        };
        var factory = new ScriptedDapClientFactory(client);
        await using var session = CreateSession(factory);
        await session.AddBreakpointAsync("/tmp/scripted-source.cs", 42);
        using var cancellation = new CancellationTokenSource(TestTimeout);

        await session.LaunchAsync(
                "/tmp/scripted-debuggee.dll",
                cancellationToken: cancellation.Token)
            .WaitAsync(TestTimeout);

        Assert.Equal(
            ["launch", "setBreakpoints", "setExceptionBreakpoints", "configurationDone"],
            client.Requests.Select(request => request.Command));
    }

    [Fact]
    public async Task LaunchAsync_UsesInitializedEventEmittedDuringStartAsync()
    {
        var client = new ScriptedDapClient
        {
            OnStart = dap => dap.EmitInitialized()
        };
        var factory = new ScriptedDapClientFactory(client);
        await using var session = CreateSession(factory);
        await session.AddBreakpointAsync("/tmp/scripted-source.cs", 42);
        using var cancellation = new CancellationTokenSource(TestTimeout);

        await session.LaunchAsync(
                "/tmp/scripted-debuggee.dll",
                cancellationToken: cancellation.Token)
            .WaitAsync(TestTimeout);

        Assert.Equal("running", session.State);
        Assert.Equal(
            ["launch", "setBreakpoints", "setExceptionBreakpoints", "configurationDone"],
            client.Requests.Select(request => request.Command));
    }

    [Fact]
    public async Task OverlappingContinueAndStep_SendOnlyOneResumeRequest()
    {
        var firstRequestReceived = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstResponse = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = CreateStoppedClient();
        client.OnRequest = async (request, cancellationToken) =>
        {
            if (request.Command == "continue")
            {
                firstRequestReceived.TrySetResult(true);
                await releaseFirstResponse.Task.WaitAsync(cancellationToken);
            }

            return ScriptedDapClient.Success(request.Command);
        };
        var factory = new ScriptedDapClientFactory(client);
        await using var session = CreateSession(factory);
        await session.EnsureStartedAsync().WaitAsync(TestTimeout);

        var continueTask = session.ContinueAsync(TestTimeout);
        await firstRequestReceived.Task.WaitAsync(TestTimeout);

        Exception? stepException;
        try
        {
            stepException = await Record.ExceptionAsync(
                () => session.StepOverAsync(TestTimeout).WaitAsync(TestTimeout));
        }
        finally
        {
            releaseFirstResponse.TrySetResult(true);
            client.EmitContinued();
            client.EmitStopped(reason: "step");
        }

        await continueTask.WaitAsync(TestTimeout);
        Assert.IsType<InvalidOperationException>(stepException);
        Assert.Equal(1, client.RequestCount("continue"));
        Assert.Equal(0, client.RequestCount("next"));
    }

    [Fact]
    public async Task AdapterClosure_TransitionsActiveSessionToTerminated()
    {
        var client = CreateStoppedClient();
        var factory = new ScriptedDapClientFactory(client);
        await using var session = CreateSession(factory);
        await session.EnsureStartedAsync().WaitAsync(TestTimeout);

        client.EmitClosed(new EndOfStreamException("adapter output closed"));

        Assert.Equal("terminated", session.State);
    }

    [Fact]
    public async Task AttachAndStop_DisconnectsWithoutTerminatingTarget()
    {
        var client = new ScriptedDapClient();
        client.OnRequest = (request, _) =>
        {
            if (request.Command == "attach")
            {
                client.EmitInitialized();
            }

            return Task.FromResult(ScriptedDapClient.Success(request.Command));
        };
        var factory = new ScriptedDapClientFactory(client);
        await using var session = CreateSession(factory);

        await session.AttachAsync(4242).WaitAsync(TestTimeout);
        await session.StopAsync().WaitAsync(TestTimeout);

        Assert.Equal(
            ["attach", "setExceptionBreakpoints", "configurationDone", "disconnect"],
            client.Requests.Select(request => request.Command));
        var disconnect = client.Requests.Single(request => request.Command == "disconnect");
        Assert.False(disconnect.Arguments!["terminateDebuggee"]!.GetValue<bool>());
        var attach = client.Requests.Single(request => request.Command == "attach");
        Assert.Equal(4242, attach.Arguments!["processId"]!.GetValue<int>());
    }

    [Fact]
    public async Task LaunchAndStop_DisconnectsAndTerminatesTarget()
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
        var factory = new ScriptedDapClientFactory(client);
        await using var session = CreateSession(factory);

        await session.LaunchAsync("/tmp/scripted-debuggee.dll").WaitAsync(TestTimeout);
        await session.StopAsync().WaitAsync(TestTimeout);

        var disconnect = client.Requests.Single(request => request.Command == "disconnect");
        Assert.True(disconnect.Arguments!["terminateDebuggee"]!.GetValue<bool>());
    }

    private static ScriptedDapClient CreateStoppedClient()
    {
        return new ScriptedDapClient
        {
            OnStart = client =>
            {
                client.EmitInitialized();
                client.EmitStopped();
            }
        };
    }

    private static DebugSession CreateSession(IDapClientFactory factory)
    {
        return new DebugSession(
            () => new BackendInfo { Path = "/fake/netcoredbg" },
            factory);
    }

    private static Task<object> InvokeResumeAsync(DebugSession session, string operation)
    {
        return operation switch
        {
            "continue" => session.ContinueAsync(TestTimeout),
            "step_over" => session.StepOverAsync(TestTimeout),
            "step_into" => session.StepIntoAsync(TestTimeout),
            "step_out" => session.StepOutAsync(TestTimeout),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
        };
    }
}
