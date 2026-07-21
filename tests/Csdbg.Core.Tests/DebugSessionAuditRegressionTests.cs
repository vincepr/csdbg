using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Csdbg.Core.Dap;

namespace Csdbg.Core.Tests;

public sealed class DebugSessionAuditRegressionTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task AttachAsync_ConfigurationFailure_DetachesAndReturnsToIdle()
    {
        var client = new ScriptedDapClient();
        client.OnRequest = (request, _) =>
        {
            if (request.Command == "attach")
            {
                client.EmitInitialized();
            }

            return Task.FromResult(
                request.Command == "setExceptionBreakpoints"
                    ? Failure(request.Command, "scripted configuration failure")
                    : ScriptedDapClient.Success(request.Command));
        };
        await using var session = CreateSession(client);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.AttachAsync(4242).WaitAsync(TestTimeout));

        Assert.Equal("scripted configuration failure", exception.Message);
        Assert.Equal("idle", session.State);
        var disconnect = Assert.Single(
            client.Requests,
            request => request.Command == "disconnect");
        Assert.False(disconnect.Arguments!["terminateDebuggee"]!.GetValue<bool>());
    }

    [Fact]
    public async Task UnsolicitedStoppedEvent_RefreshesCurrentLocationOnce()
    {
        var sourcePath = await CreateSourceFileAsync();
        try
        {
            var client = CreateLaunchClient();
            client.OnRequest = (request, _) =>
            {
                if (request.Command == "launch")
                {
                    client.EmitInitialized();
                }

                return Task.FromResult(
                    request.Command == "stackTrace"
                        ? StackTraceSuccess(sourcePath, 4, "Program.UnsolicitedStop")
                        : ScriptedDapClient.Success(request.Command));
            };
            await using var session = CreateSession(client);
            await session.LaunchAsync("/tmp/scripted-debuggee.dll").WaitAsync(TestTimeout);
            Assert.Equal("running", session.State);

            client.EmitStopped(threadId: 7, reason: "breakpoint");

            await client.WaitForRequestAsync("stackTrace", TestTimeout);
            await WaitUntilAsync(
                () => CurrentLocation(session)?["file"]?.GetValue<string>() == sourcePath,
                TestTimeout);
            Assert.Equal("stopped", session.State);
            Assert.Equal(1, client.RequestCount("stackTrace"));
            AssertCurrentLocation(session, sourcePath, 4, "Program.UnsolicitedStop");
            var context = Assert.IsType<JsonObject>(CurrentLocation(session)!["context"]);
            Assert.Equal(4, context["CurrentLine"]!.GetValue<int>());
            Assert.Equal("line 4", context["Lines"]![3]!["Text"]!.GetValue<string>());
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [Fact]
    public async Task ContinueAsync_StoppedWithFailedStackTrace_ReturnsStoppedWithoutTimingOut()
    {
        var client = CreateLaunchClient();
        client.OnRequest = (request, _) =>
        {
            if (request.Command == "launch")
            {
                client.EmitInitialized();
            }
            else if (request.Command == "continue")
            {
                client.EmitContinued(threadId: 7);
                client.EmitStopped(threadId: 7, reason: "step");
            }

            if (request.Command == "stackTrace" && client.RequestCount("stackTrace") == 2)
            {
                return Task.FromResult(Failure("stackTrace", "scripted stack trace failure"));
            }

            return Task.FromResult(
                request.Command == "stackTrace"
                    ? StackTraceSuccess("/tmp/initial-source.cs", 3, "Program.Initial")
                    : ScriptedDapClient.Success(request.Command));
        };
        await using var session = CreateSession(client);
        await session.LaunchAsync("/tmp/scripted-debuggee.dll").WaitAsync(TestTimeout);
        client.EmitStopped(threadId: 7);
        await WaitUntilAsync(() => client.RequestCount("stackTrace") == 1, TestTimeout);

        var result = await session.ContinueAsync(TimeSpan.FromMilliseconds(250)).WaitAsync(TestTimeout);

        var resultJson = Json(result);
        Assert.False(resultJson["timedOut"]!.GetValue<bool>());
        Assert.Equal("stopped", resultJson["status"]!["state"]!.GetValue<string>());
        Assert.Equal("stopped", session.State);
        Assert.Equal(2, client.RequestCount("stackTrace"));
    }

    [Fact]
    public async Task ContinueAsync_SlowStackTraceAfterRealStop_DoesNotReportTimeout()
    {
        var slowStackTrace = new TaskCompletionSource<JsonObject>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = CreateLaunchClient();
        client.OnRequest = async (request, cancellationToken) =>
        {
            if (request.Command == "launch")
            {
                client.EmitInitialized();
            }
            else if (request.Command == "continue")
            {
                client.EmitContinued(threadId: 9);
                client.EmitStopped(threadId: 9, reason: "step");
            }

            if (request.Command == "stackTrace" && client.RequestCount("stackTrace") == 2)
            {
                return await slowStackTrace.Task.WaitAsync(cancellationToken);
            }

            return request.Command == "stackTrace"
                ? StackTraceSuccess("/tmp/initial-source.cs", 3, "Program.Initial")
                : ScriptedDapClient.Success(request.Command);
        };
        await using var session = CreateSession(client);
        await session.LaunchAsync("/tmp/scripted-debuggee.dll").WaitAsync(TestTimeout);
        client.EmitStopped(threadId: 9);
        await WaitUntilAsync(() => client.RequestCount("stackTrace") == 1, TestTimeout);

        var continueTask = session.ContinueAsync(TimeSpan.Zero);
        await WaitUntilAsync(() => client.RequestCount("stackTrace") == 2, TestTimeout);

        try
        {
            var result = await continueTask.WaitAsync(TestTimeout);
            var resultJson = Json(result);
            Assert.False(resultJson["timedOut"]!.GetValue<bool>());
            Assert.Equal("stopped", resultJson["status"]!["state"]!.GetValue<string>());
        }
        finally
        {
            slowStackTrace.TrySetResult(
                StackTraceSuccess("/tmp/slow-source.cs", 8, "Program.Slow"));
        }
    }

    [Fact]
    public async Task DelayedStackTrace_FromPreviousStop_DoesNotOverwriteNewLocation()
    {
        var staleStackTrace = new TaskCompletionSource<JsonObject>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = CreateLaunchClient();
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

            return client.RequestCount("stackTrace") == 1
                ? staleStackTrace.Task
                : Task.FromResult(StackTraceSuccess("/tmp/new-source.cs", 22, "Program.NewStop"));
        };
        await using var session = CreateSession(client);
        await session.LaunchAsync("/tmp/scripted-debuggee.dll").WaitAsync(TestTimeout);

        client.EmitStopped(threadId: 1, reason: "breakpoint");
        await WaitUntilAsync(() => client.RequestCount("stackTrace") == 1, TestTimeout);
        var staleRefreshTask = GetLocationRefreshTask(session);

        client.EmitContinued(threadId: 1);
        client.EmitStopped(threadId: 2, reason: "step");
        await WaitUntilAsync(
            () => CurrentLocation(session)?["file"]?.GetValue<string>() == "/tmp/new-source.cs",
            TestTimeout);

        staleStackTrace.TrySetResult(
            StackTraceSuccess("/tmp/stale-source.cs", 11, "Program.StaleStop"));
        await staleRefreshTask.WaitAsync(TestTimeout);

        Assert.Equal(2, client.RequestCount("stackTrace"));
        AssertCurrentLocation(session, "/tmp/new-source.cs", 22, "Program.NewStop");
    }

    private static ScriptedDapClient CreateLaunchClient() => new();

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

    private static JsonObject StackTraceSuccess(
        string sourcePath,
        int line,
        string frameName) =>
        ScriptedDapClient.Success("stackTrace", new JsonObject
        {
            ["stackFrames"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = 1,
                    ["name"] = frameName,
                    ["line"] = line,
                    ["source"] = new JsonObject { ["path"] = sourcePath }
                }
            },
            ["totalFrames"] = 1
        });

    private static async Task<string> CreateSourceFileAsync()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"csdbg-session-audit-{Guid.NewGuid():N}.cs");
        await File.WriteAllLinesAsync(
            path,
            Enumerable.Range(1, 8).Select(number => $"line {number}"));
        return path;
    }

    private static void AssertCurrentLocation(
        DebugSession session,
        string sourcePath,
        int line,
        string frameName)
    {
        var location = Assert.IsType<JsonObject>(CurrentLocation(session));
        Assert.Equal(sourcePath, location["file"]!.GetValue<string>());
        Assert.Equal(line, location["line"]!.GetValue<int>());
        Assert.Equal(frameName, location["frame"]!.GetValue<string>());
    }

    private static JsonObject? CurrentLocation(DebugSession session) =>
        Json(session.GetStatus())["currentLocation"] as JsonObject;

    private static JsonObject Json(object value) =>
        JsonSerializer.SerializeToNode(value)!.AsObject();

    private static Task GetLocationRefreshTask(DebugSession session) =>
        Assert.IsAssignableFrom<Task>(
            typeof(DebugSession)
                .GetField("_locationRefreshTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(session));

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellation.Token);
        }
    }
}
