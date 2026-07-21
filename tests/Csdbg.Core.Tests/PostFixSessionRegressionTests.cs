using System.Text.Json;
using System.Text.Json.Nodes;
using Csdbg.Core.Dap;

namespace Csdbg.Core.Tests;

public sealed class PostFixSessionRegressionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task LaunchAsync_ImmediateRejectionWithoutInitialized_FailsPromptlyAndReturnsToIdle()
    {
        var client = new ScriptedDapClient
        {
            OnRequest = (request, _) => Task.FromResult(
                request.Command == "launch"
                    ? Failure("launch", "scripted immediate launch rejection")
                    : ScriptedDapClient.Success(request.Command))
        };
        await using var session = CreateSession(client);

        var exception = await AssertFailsPromptlyAsync(
            cancellationToken => session.LaunchAsync(
                "/tmp/scripted-debuggee.dll",
                cancellationToken: cancellationToken));

        Assert.Equal("scripted immediate launch rejection", exception.Message);
        Assert.Equal("idle", session.State);
        Assert.Equal(1, client.RequestCount("disconnect"));
        Assert.True(DisconnectTerminatesDebuggee(client));
        Assert.Equal(1, client.DisposeCount);
    }

    [Fact]
    public async Task AttachAsync_ImmediateRejectionWithoutInitialized_FailsPromptlyAndReturnsToIdle()
    {
        var client = new ScriptedDapClient
        {
            OnRequest = (request, _) => Task.FromResult(
                request.Command == "attach"
                    ? Failure("attach", "scripted immediate attach rejection")
                    : ScriptedDapClient.Success(request.Command))
        };
        await using var session = CreateSession(client);

        var exception = await AssertFailsPromptlyAsync(
            cancellationToken => session.AttachAsync(4242, cancellationToken));

        Assert.Equal("scripted immediate attach rejection", exception.Message);
        Assert.Equal("idle", session.State);
        Assert.Equal(1, client.RequestCount("disconnect"));
        Assert.False(DisconnectTerminatesDebuggee(client));
        Assert.Equal(1, client.DisposeCount);
    }

    [Fact]
    public async Task SecondStopOnAnotherThread_FailedStackTraceClearsPreviousLocationAndContext()
    {
        var sourcePath = await CreateSourceFileAsync();
        try
        {
            var secondStackTraceRequested = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var client = new ScriptedDapClient();
            client.OnRequest = (request, _) =>
            {
                if (request.Command == "launch")
                {
                    client.EmitInitialized();
                }

                if (request.Command != "stackTrace")
                {
                    return Task.FromResult(ScriptedDapClient.Success(request.Command));
                }

                if (client.RequestCount("stackTrace") == 1)
                {
                    return Task.FromResult(StackTraceSuccess(sourcePath));
                }

                secondStackTraceRequested.TrySetResult();
                return Task.FromResult(Failure("stackTrace", "scripted stack trace failure"));
            };
            await using var session = CreateSession(client);
            await session.LaunchAsync("/tmp/scripted-debuggee.dll").WaitAsync(TestTimeout);

            client.EmitStopped(threadId: 11, reason: "breakpoint");
            await WaitUntilAsync(
                () => CurrentLocation(session)?["context"] is JsonObject,
                TestTimeout);

            var firstLocation = Assert.IsType<JsonObject>(CurrentLocation(session));
            Assert.Equal(sourcePath, firstLocation["file"]!.GetValue<string>());
            Assert.IsType<JsonObject>(firstLocation["context"]);

            client.EmitStopped(threadId: 22, reason: "exception");
            await secondStackTraceRequested.Task.WaitAsync(TestTimeout);

            Assert.Equal("stopped", session.State);
            Assert.Equal(22, session.CurrentThreadId);
            Assert.Equal(2, client.RequestCount("stackTrace"));
            Assert.Null(CurrentLocation(session));
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    private static DebugSession CreateSession(ScriptedDapClient client) =>
        new(
            () => new BackendInfo { Path = "/fake/netcoredbg" },
            new ScriptedDapClientFactory(client));

    private static JsonObject Failure(string command, string message) => new()
    {
        ["type"] = "response",
        ["command"] = command,
        ["success"] = false,
        ["message"] = message
    };

    private static JsonObject StackTraceSuccess(string sourcePath) =>
        ScriptedDapClient.Success("stackTrace", new JsonObject
        {
            ["stackFrames"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = 1,
                    ["name"] = "Program.FirstStop",
                    ["line"] = 4,
                    ["source"] = new JsonObject { ["path"] = sourcePath }
                }
            },
            ["totalFrames"] = 1
        });

    private static bool DisconnectTerminatesDebuggee(ScriptedDapClient client) =>
        client.Requests
            .Single(request => request.Command == "disconnect")
            .Arguments!["terminateDebuggee"]!
            .GetValue<bool>();

    private static JsonObject? CurrentLocation(DebugSession session) =>
        JsonSerializer.SerializeToNode(session.GetStatus())!["currentLocation"] as JsonObject;

    private static async Task<InvalidOperationException> AssertFailsPromptlyAsync(
        Func<CancellationToken, Task<object>> operation)
    {
        using var cancellation = new CancellationTokenSource();
        var operationTask = operation(cancellation.Token);
        var completedTask = await Task.WhenAny(operationTask, Task.Delay(TestTimeout));
        if (completedTask != operationTask)
        {
            cancellation.Cancel();
            try
            {
                await operationTask.WaitAsync(TestTimeout);
            }
            catch
            {
                // Drain cancellation so failed regressions do not leave a 30-second wait behind.
            }

            Assert.Fail("The immediate DAP rejection was not surfaced promptly.");
        }

        return await Assert.ThrowsAsync<InvalidOperationException>(() => operationTask);
    }

    private static async Task<string> CreateSourceFileAsync()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"csdbg-post-fix-{Guid.NewGuid():N}.cs");
        await File.WriteAllLinesAsync(
            path,
            Enumerable.Range(1, 8).Select(number => $"line {number}"));
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellation.Token);
        }
    }
}
