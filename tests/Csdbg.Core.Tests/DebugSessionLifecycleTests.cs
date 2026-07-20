using System.Text.Json.Nodes;
using Csdbg.Core.Dap;

namespace Csdbg.Core.Tests;

public sealed class DebugSessionLifecycleTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IncompleteCheckTimeout = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task StopAsync_DuringDeferredLaunch_WaitsThenCleansUpExactlyOnce()
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

            return ScriptedDapClient.Success(request.Command);
        };
        await using var session = CreateSession(new QueueDapClientFactory(client));

        var launchTask = session.LaunchAsync("/tmp/scripted-debuggee.dll");
        await client.WaitForRequestAsync("configurationDone", TestTimeout);
        var stopTask = session.StopAsync();

        try
        {
            await AssertIncompleteAsync(stopTask);
            Assert.Equal(0, client.RequestCount("disconnect"));
            Assert.Equal(0, client.DisposeCount);
        }
        finally
        {
            launchResponse.TrySetResult(ScriptedDapClient.Success("launch"));
        }

        await launchTask.WaitAsync(TestTimeout);
        await stopTask.WaitAsync(TestTimeout);

        Assert.Equal("idle", session.State);
        Assert.Equal(1, client.RequestCount("disconnect"));
        Assert.Equal(1, client.DisposeCount);
        Assert.True(DisconnectTerminatesDebuggee(client));
    }

    [Fact]
    public async Task StopAsync_DuringDeferredAttach_WaitsThenDetachesAndCleansUpExactlyOnce()
    {
        var attachResponse = new TaskCompletionSource<JsonObject>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new ScriptedDapClient();
        client.OnRequest = async (request, cancellationToken) =>
        {
            if (request.Command == "attach")
            {
                client.EmitInitialized();
                return await attachResponse.Task.WaitAsync(cancellationToken);
            }

            return ScriptedDapClient.Success(request.Command);
        };
        await using var session = CreateSession(new QueueDapClientFactory(client));

        var attachTask = session.AttachAsync(4242);
        await client.WaitForRequestAsync("configurationDone", TestTimeout);
        var stopTask = session.StopAsync();

        try
        {
            await AssertIncompleteAsync(stopTask);
            Assert.Equal(0, client.RequestCount("disconnect"));
            Assert.Equal(0, client.DisposeCount);
        }
        finally
        {
            attachResponse.TrySetResult(ScriptedDapClient.Success("attach"));
        }

        await attachTask.WaitAsync(TestTimeout);
        await stopTask.WaitAsync(TestTimeout);

        Assert.Equal("idle", session.State);
        Assert.Equal(1, client.RequestCount("disconnect"));
        Assert.Equal(1, client.DisposeCount);
        Assert.False(DisconnectTerminatesDebuggee(client));
    }

    [Fact]
    public async Task ConcurrentStopAsync_SerializesAndCleansUpExactlyOnce()
    {
        var disconnectResponse = new TaskCompletionSource<JsonObject>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = CreateLaunchClient();
        client.OnRequest = async (request, cancellationToken) =>
        {
            if (request.Command == "launch")
            {
                client.EmitInitialized();
            }
            else if (request.Command == "disconnect")
            {
                return await disconnectResponse.Task.WaitAsync(cancellationToken);
            }

            return ScriptedDapClient.Success(request.Command);
        };
        await using var session = CreateSession(new QueueDapClientFactory(client));
        await session.LaunchAsync("/tmp/scripted-debuggee.dll").WaitAsync(TestTimeout);

        var firstStop = session.StopAsync();
        await client.WaitForRequestAsync("disconnect", TestTimeout);
        var secondStop = session.StopAsync();

        try
        {
            await AssertIncompleteAsync(firstStop);
            await AssertIncompleteAsync(secondStop);
            Assert.Equal(1, client.RequestCount("disconnect"));
            Assert.Equal(0, client.DisposeCount);
        }
        finally
        {
            disconnectResponse.TrySetResult(ScriptedDapClient.Success("disconnect"));
        }

        await Task.WhenAll(firstStop, secondStop).WaitAsync(TestTimeout);

        Assert.Equal("idle", session.State);
        Assert.Equal(1, client.RequestCount("disconnect"));
        Assert.Equal(1, client.DisposeCount);
    }

    [Fact]
    public async Task EnsureStartedAsync_FailedStartDisposesAndRetryUsesFreshClient()
    {
        var failedClient = new ScriptedDapClient
        {
            OnStart = _ => throw new InvalidOperationException("scripted start failure")
        };
        var retryClient = new ScriptedDapClient
        {
            OnStart = client => client.EmitInitialized()
        };
        var factory = new QueueDapClientFactory(failedClient, retryClient);
        await using var session = CreateSession(factory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.EnsureStartedAsync().WaitAsync(TestTimeout));

        Assert.Equal("scripted start failure", exception.Message);
        Assert.Equal("idle", session.State);
        Assert.Equal(1, failedClient.StartCount);
        Assert.Equal(1, failedClient.DisposeCount);
        Assert.False(failedClient.IsRunning);

        await session.EnsureStartedAsync().WaitAsync(TestTimeout);

        Assert.Equal(2, factory.CreateCount);
        Assert.Equal(1, retryClient.StartCount);
        Assert.True(retryClient.IsRunning);
        await session.StopAsync().WaitAsync(TestTimeout);
    }

    [Theory]
    [InlineData("launch")]
    [InlineData("configurationDone")]
    public async Task LaunchAsync_FailedLaunchOrConfigurationCleansUpAndPermitsRetry(
        string failedCommand)
    {
        var failedClient = new ScriptedDapClient();
        failedClient.OnRequest = (request, _) =>
        {
            if (request.Command == "launch")
            {
                failedClient.EmitInitialized();
            }

            return Task.FromResult(
                request.Command == failedCommand
                    ? Failure(request.Command)
                    : ScriptedDapClient.Success(request.Command));
        };
        var retryClient = CreateLaunchClient();
        var factory = new QueueDapClientFactory(failedClient, retryClient);
        await using var session = CreateSession(factory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.LaunchAsync("/tmp/first-debuggee.dll").WaitAsync(TestTimeout));

        Assert.Equal($"scripted {failedCommand} failure", exception.Message);
        Assert.Equal("idle", session.State);
        Assert.Equal(1, failedClient.RequestCount("disconnect"));
        Assert.Equal(1, failedClient.DisposeCount);
        Assert.False(failedClient.IsRunning);

        await session.LaunchAsync("/tmp/retry-debuggee.dll").WaitAsync(TestTimeout);

        Assert.Equal("running", session.State);
        Assert.Equal(2, factory.CreateCount);
        Assert.Equal(1, retryClient.StartCount);
        await session.StopAsync().WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task CanceledLifecycleWaiter_DoesNotReleaseGateItDidNotAcquire()
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

            return ScriptedDapClient.Success(request.Command);
        };
        await using var session = CreateSession(new QueueDapClientFactory(client));

        var launchTask = session.LaunchAsync("/tmp/scripted-debuggee.dll");
        await client.WaitForRequestAsync("configurationDone", TestTimeout);
        using var cancellation = new CancellationTokenSource();
        var waitingStart = session.EnsureStartedAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => waitingStart.WaitAsync(TestTimeout));
        var stopTask = session.StopAsync();

        try
        {
            await AssertIncompleteAsync(stopTask);
            Assert.Equal(0, client.RequestCount("disconnect"));
            Assert.Equal(0, client.DisposeCount);
        }
        finally
        {
            launchResponse.TrySetResult(ScriptedDapClient.Success("launch"));
        }

        await launchTask.WaitAsync(TestTimeout);
        await stopTask.WaitAsync(TestTimeout);

        Assert.Equal(1, client.RequestCount("disconnect"));
        Assert.Equal(1, client.DisposeCount);
    }

    private static ScriptedDapClient CreateLaunchClient()
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
        return client;
    }

    private static DebugSession CreateSession(IDapClientFactory factory)
    {
        return new DebugSession(
            () => new BackendInfo { Path = "/fake/netcoredbg" },
            factory);
    }

    private static JsonObject Failure(string command)
    {
        return new JsonObject
        {
            ["type"] = "response",
            ["command"] = command,
            ["success"] = false,
            ["message"] = $"scripted {command} failure"
        };
    }

    private static bool DisconnectTerminatesDebuggee(ScriptedDapClient client)
    {
        return client.Requests
            .Single(request => request.Command == "disconnect")
            .Arguments!["terminateDebuggee"]!
            .GetValue<bool>();
    }

    private static async Task AssertIncompleteAsync(Task task)
    {
        await Assert.ThrowsAsync<TimeoutException>(
            () => task.WaitAsync(IncompleteCheckTimeout));
    }

    private sealed class QueueDapClientFactory(params ScriptedDapClient[] clients) : IDapClientFactory
    {
        private readonly Queue<ScriptedDapClient> _clients = new(clients);

        public int CreateCount { get; private set; }

        public IDapClient Create(string netcoredbgPath)
        {
            CreateCount++;
            return _clients.Dequeue();
        }
    }
}
