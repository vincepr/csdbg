using System.Text.Json.Nodes;
using Csdbg.Core.Dap;

namespace Csdbg.Core.Tests;

public sealed class DapClientTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task SendRequestAsync_DeferredResponse_DoesNotBlockLaterRequestWrite()
    {
        var (client, process) = await StartClientAsync();
        await using var cleanup = client;

        var launchTask = client.SendRequestAsync("launch");
        var launch = await ReadRequestAsync(process);
        Assert.Equal("launch", launch["command"]?.GetValue<string>());

        var configurationDoneTask = client.SendRequestAsync("configurationDone");
        var configurationDone = await ReadRequestAsync(process);
        Assert.Equal("configurationDone", configurationDone["command"]?.GetValue<string>());
        Assert.False(launchTask.IsCompleted);

        await process.SendResponseAsync(configurationDone).WaitAsync(TestTimeout);
        await configurationDoneTask.WaitAsync(TestTimeout);
        Assert.False(launchTask.IsCompleted);

        await process.SendResponseAsync(launch).WaitAsync(TestTimeout);
        await launchTask.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task StartAsync_InitializeRejected_ThrowsAndCleansUpProcess()
    {
        var process = new ScriptedDapProcess();
        var factory = new ScriptedDapProcessFactory(process);
        var client = new DapClient("/fake/netcoredbg", factory, RequestTimeout);

        var startTask = client.StartAsync();
        var initialize = await ReadRequestAsync(process);
        await process.SendResponseAsync(
            initialize,
            success: false,
            message: "scripted initialization rejection").WaitAsync(TestTimeout);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => startTask.WaitAsync(TestTimeout));

        Assert.Equal("scripted initialization rejection", exception.Message);
        Assert.False(client.IsRunning);
        Assert.Equal(1, factory.StartCount);
        Assert.Equal(1, process.KillCount);
        Assert.Equal(1, process.DisposeCount);
    }

    [Fact]
    public async Task SendRequestAsync_WriteFailure_RemovesPendingRequest()
    {
        var (client, process) = await StartClientAsync();
        await using var cleanup = client;
        process.FailNextInputWrite(new IOException("scripted write failure"));

        var exception = await Assert.ThrowsAsync<IOException>(
            () => client.SendRequestAsync("writeFailure").WaitAsync(TestTimeout));

        Assert.Equal("scripted write failure", exception.Message);
        Assert.Equal(0, client.PendingRequestCount);
        Assert.False(client.IsRunning);
        Assert.Equal(1, process.KillCount);

        var writeCount = process.InputWriteCount;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendRequestAsync("mustNotWrite"));
        Assert.Equal(writeCount, process.InputWriteCount);
    }

    [Fact]
    public async Task SendRequestAsync_CancellationDuringFrameWrite_FaultsTransportAndPreventsLaterWrites()
    {
        var (client, process) = await StartClientAsync();
        await using var cleanup = client;
        using var cancellation = new CancellationTokenSource();
        var writeStarted = process.BlockNextInputWriteUntilCanceled();

        var requestTask = client.SendRequestAsync(
            "cancelDuringWrite",
            cancellationToken: cancellation.Token);
        await writeStarted.WaitAsync(TestTimeout);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => requestTask.WaitAsync(TestTimeout));
        Assert.False(client.IsRunning);
        Assert.Equal(0, client.PendingRequestCount);
        Assert.Equal(1, process.KillCount);

        var writeCount = process.InputWriteCount;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendRequestAsync("mustNotWrite"));
        Assert.Equal(writeCount, process.InputWriteCount);
    }

    [Fact]
    public async Task SendRequestAsync_CallerCancellation_RemovesPendingAndLaterRequestSucceeds()
    {
        var (client, process) = await StartClientAsync();
        await using var cleanup = client;
        using var cancellation = new CancellationTokenSource();

        var canceledRequest = client.SendRequestAsync(
            "cancelMe",
            cancellationToken: cancellation.Token);
        var request = await ReadRequestAsync(process);
        Assert.Equal("cancelMe", request["command"]?.GetValue<string>());

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceledRequest.WaitAsync(TestTimeout));
        Assert.Equal(0, client.PendingRequestCount);

        var laterRequest = client.SendRequestAsync("stillAlive");
        request = await ReadRequestAsync(process);
        await process.SendResponseAsync(request).WaitAsync(TestTimeout);

        var response = await laterRequest.WaitAsync(TestTimeout);
        Assert.Equal("stillAlive", response["command"]?.GetValue<string>());
        Assert.True(client.IsRunning);
    }

    [Fact]
    public async Task SendRequestAsync_Timeout_RemovesPendingRequest()
    {
        var (client, process) = await StartClientAsync(RequestTimeout);
        await using var cleanup = client;

        var requestTask = client.SendRequestAsync("neverRespond");
        var request = await ReadRequestAsync(process);
        Assert.Equal("neverRespond", request["command"]?.GetValue<string>());

        await Assert.ThrowsAsync<TimeoutException>(
            () => requestTask.WaitAsync(TestTimeout));
        Assert.Equal(0, client.PendingRequestCount);
    }

    [Fact]
    public async Task CleanOutputEof_FailsPendingRequestAndRaisesClosed()
    {
        var (client, process) = await StartClientAsync();
        await using var cleanup = client;
        var closed = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.Closed += exception => closed.TrySetResult(exception);

        var requestTask = client.SendRequestAsync("pendingAtEof");
        await ReadRequestAsync(process);
        await process.CompleteOutputAsync().WaitAsync(TestTimeout);

        var requestException = await Assert.ThrowsAsync<EndOfStreamException>(
            () => requestTask.WaitAsync(TestTimeout));
        var closedException = await closed.Task.WaitAsync(TestTimeout);

        Assert.Contains("output", requestException.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(requestException, closedException);
        Assert.Equal(0, client.PendingRequestCount);
    }

    [Fact]
    public async Task MalformedInboundFrame_FaultsClientAndLaterRequestRejectsImmediately()
    {
        var (client, process) = await StartClientAsync();
        await using var cleanup = client;
        var closed = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.Closed += exception => closed.TrySetResult(exception);

        await process.WriteRawOutputAsync("Content-Length: nope\r\n\r\n").WaitAsync(TestTimeout);

        var exception = await closed.Task.WaitAsync(TestTimeout);
        Assert.IsType<InvalidDataException>(exception);
        Assert.False(client.IsRunning);
        Assert.Equal(1, process.KillCount);

        var writeCount = process.InputWriteCount;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendRequestAsync("mustNotWrite").WaitAsync(TestTimeout));
        Assert.Equal(writeCount, process.InputWriteCount);
    }

    [Fact]
    public async Task StandardError_IsContinuouslyDrained()
    {
        var (client, process) = await StartClientAsync();
        await using var cleanup = client;
        var stderr = new string('x', 256 * 1024);

        await process.WriteStandardErrorAsync(stderr).WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var (client, process) = await StartClientAsync();

        await client.DisposeAsync().AsTask().WaitAsync(TestTimeout);
        await client.DisposeAsync().AsTask().WaitAsync(TestTimeout);

        Assert.Equal(1, process.KillCount);
        Assert.Equal(1, process.DisposeCount);
    }

    private static async Task<(DapClient Client, ScriptedDapProcess Process)> StartClientAsync(
        TimeSpan? requestTimeout = null)
    {
        var process = new ScriptedDapProcess();
        var factory = new ScriptedDapProcessFactory(process);
        var client = new DapClient(
            "/fake/netcoredbg",
            factory,
            requestTimeout ?? TimeSpan.FromSeconds(1));

        var startTask = client.StartAsync();
        var initialize = await ReadRequestAsync(process);
        Assert.Equal("initialize", initialize["command"]?.GetValue<string>());
        await process.SendResponseAsync(initialize).WaitAsync(TestTimeout);
        await startTask.WaitAsync(TestTimeout);
        Assert.Equal(1, factory.StartCount);
        return (client, process);
    }

    private static async Task<JsonObject> ReadRequestAsync(ScriptedDapProcess process)
    {
        var request = await process.ReadRequestAsync().WaitAsync(TestTimeout);
        return Assert.IsType<JsonObject>(request);
    }
}
