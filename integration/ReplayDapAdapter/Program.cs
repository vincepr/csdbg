using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Csdbg.Core.Dap;

AdapterDiagnostics.SuppressWindowsErrorDialogs();

const string evidenceDirectoryVariable = "CSDBG_REPLAY_EVIDENCE_DIR";
var evidenceDirectory = Environment.GetEnvironmentVariable(evidenceDirectoryVariable)
    ?? throw new InvalidOperationException($"{evidenceDirectoryVariable} is required.");
Directory.CreateDirectory(evidenceDirectory);
var diagnostics = new AdapterDiagnostics(evidenceDirectory);
diagnostics.Track("claim-session");

var session = ClaimSession(evidenceDirectory);
diagnostics.Track("adapter-start", session);
var evidencePath = Path.Combine(evidenceDirectory, "evidence.jsonl");
AppendEvidence(evidencePath, new JsonObject
{
    ["kind"] = "adapter-start",
    ["session"] = session,
    ["pid"] = Environment.ProcessId
});

var fixture = ReadFixture();
var allInteractions = fixture["dapInteractions"]!.AsArray();
var offset = session switch
{
    0 => 0,
    1 => 18,
    _ => throw new InvalidOperationException($"Unexpected replay session {session}.")
};
var count = session == 0 ? 18 : 7;
var interactions = new Queue<JsonObject>(
    allInteractions.Skip(offset).Take(count).Select(item => item!.AsObject()));
var output = Console.OpenStandardOutput();
var input = Console.OpenStandardInput();
var writeLock = new SemaphoreSlim(1, 1);
var responseSequence = 0;
Process? target = null;
StopReleaseWorker? stopRelease = null;

try
{
    diagnostics.Track("read-loop", session);
    while (await DapMessageFraming.ReadAsync(input) is { } request)
    {
        var command = request["command"]!.GetValue<string>();
        diagnostics.Track("request", session, command);
        if (command == "initialize")
        {
            diagnostics.Track("initialize-response", session, command);
            await SendAsync(Response(request, success: true, new JsonObject()));
            diagnostics.Track("initialize-response-complete", session, command);
            diagnostics.Track("initialized-event", session, command);
            await SendAsync(Event("initialized"));
            diagnostics.Track("initialized-event-complete", session, command);
            continue;
        }

        if (command == "disconnect")
        {
            var matchesReplay = interactions.TryPeek(out var next)
                && next["command"]?.GetValue<string>() == "disconnect";
            var disconnectInteraction = matchesReplay
                ? interactions.Dequeue()
                : null;
            AppendEvidence(evidencePath, new JsonObject
            {
                ["kind"] = matchesReplay ? "dap-request" : "cleanup-disconnect",
                ["session"] = session,
                ["command"] = command
            });

            stopRelease?.Dispose();
            stopRelease = null;
            diagnostics.Track("target-stop", session, command);
            StopTarget(target, evidencePath, session);
            target = null;
            diagnostics.Track("normal-exit-commitment", session, command);
            AppendEvidence(evidencePath, new JsonObject
            {
                ["kind"] = "adapter-exit",
                ["session"] = session,
                ["pid"] = Environment.ProcessId,
                ["commitment"] = "normal-exit"
            });
            var disconnectBody =
                disconnectInteraction?["body"]?.DeepClone().AsObject() ?? new JsonObject();
            disconnectBody["cleanupComplete"] = true;
            diagnostics.Track("disconnect-response", session, command);
            SendSynchronously(Response(
                request,
                disconnectInteraction?["success"]?.GetValue<bool>() ?? true,
                disconnectBody,
                disconnectInteraction?["message"]?.GetValue<string>()));
            diagnostics.Track("disconnect-response-complete", session, command);
            break;
        }

        if (interactions.Count == 0)
        {
            throw new InvalidOperationException($"Unexpected DAP command '{command}'.");
        }

        var interaction = interactions.Dequeue();
        var expected = interaction["command"]!.GetValue<string>();
        if (!string.Equals(command, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected DAP command '{expected}' but received '{command}'.");
        }

        AppendEvidence(evidencePath, new JsonObject
        {
            ["kind"] = "dap-request",
            ["session"] = session,
            ["command"] = command
        });

        if (command == "launch")
        {
            diagnostics.Track("target-start", session, command);
            target = StartTarget(evidencePath, session);
        }
        diagnostics.Track("response", session, command);
        await SendAsync(Response(
            request,
            interaction["success"]?.GetValue<bool>() ?? true,
            interaction["body"]?.DeepClone().AsObject() ?? new JsonObject(),
            interaction["message"]?.GetValue<string>()));
        diagnostics.Track("response-complete", session, command);

        foreach (var eventNode in interaction["events"]?.AsArray() ?? [])
        {
            var replayEvent = eventNode!.AsObject();
            diagnostics.Track(
                $"event:{replayEvent["event"]!.GetValue<string>()}",
                session,
                command);
            await SendAsync(Event(
                replayEvent["event"]!.GetValue<string>(),
                replayEvent["body"]?.DeepClone().AsObject()));
        }

        if (command == "configurationDone")
        {
            diagnostics.Track("stop-release-start", session, command);
            stopRelease = new StopReleaseWorker(
                Path.Combine(evidenceDirectory, $"release-stop-{session}"),
                () => SendAsync(Event("stopped", new JsonObject
                {
                    ["reason"] = "breakpoint",
                    ["threadId"] = 1,
                    ["allThreadsStopped"] = true
                })),
                diagnostics,
                session);
            stopRelease.Start();
        }
    }
}
catch (Exception ex)
{
    diagnostics.RecordException("top-level-fault", ex);
    throw;
}
finally
{
    diagnostics.Track("adapter-finally", session);
    stopRelease?.Dispose();
    StopTarget(target, evidencePath, session);
}

async Task SendAsync(JsonObject message)
{
    await writeLock.WaitAsync();
    try
    {
        message["seq"] = Interlocked.Increment(ref responseSequence);
        await DapMessageFraming.WriteAsync(output, message);
    }
    finally
    {
        writeLock.Release();
    }
}

void SendSynchronously(JsonObject message)
{
    writeLock.Wait();
    try
    {
        message["seq"] = Interlocked.Increment(ref responseSequence);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        output.Write(header);
        output.Write(payload);
        output.Flush();
    }
    finally
    {
        writeLock.Release();
    }
}

static JsonObject Response(
    JsonObject request,
    bool success,
    JsonObject body,
    string? message = null)
{
    var response = new JsonObject
    {
        ["type"] = "response",
        ["request_seq"] = request["seq"]!.GetValue<int>(),
        ["command"] = request["command"]!.GetValue<string>(),
        ["success"] = success,
        ["body"] = body
    };
    if (message is not null)
    {
        response["message"] = message;
    }

    return response;
}

static JsonObject Event(string name, JsonObject? body = null) =>
    new()
    {
        ["type"] = "event",
        ["event"] = name,
        ["body"] = body ?? new JsonObject()
    };

static JsonObject ReadFixture()
{
    var assembly = Assembly.GetExecutingAssembly();
    var resource = assembly.GetManifestResourceNames()
        .Single(name => name.EndsWith("scheduler-replay.json", StringComparison.Ordinal));
    using var stream = assembly.GetManifestResourceStream(resource)
        ?? throw new InvalidOperationException("Replay fixture resource is missing.");
    return JsonNode.Parse(stream)!.AsObject();
}

static int ClaimSession(string evidenceDirectory)
{
    var counterPath = Path.Combine(evidenceDirectory, "session-counter");
    var session = File.Exists(counterPath)
        ? int.Parse(File.ReadAllText(counterPath), System.Globalization.CultureInfo.InvariantCulture)
        : 0;
    File.WriteAllText(
        counterPath,
        (session + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
    return session;
}

static Process StartTarget(string evidencePath, int session)
{
    var executable = Path.Combine(
        AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? "SchedulerReplay.exe" : "SchedulerReplay");
    var startInfo = new ProcessStartInfo(executable)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    startInfo.ArgumentList.Add("--adapter-owned-target");
    var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Failed to start SchedulerReplay.");
    var ready = process.StandardOutput.ReadLine();
    if (ready != "ready:target")
    {
        process.Kill(entireProcessTree: true);
        throw new InvalidOperationException($"SchedulerReplay target did not become ready: {ready}");
    }

    AppendEvidence(evidencePath, new JsonObject
    {
        ["kind"] = "target-start",
        ["session"] = session,
        ["pid"] = process.Id
    });
    return process;
}

static void StopTarget(Process? process, string evidencePath, int session)
{
    if (process is null)
    {
        return;
    }

    if (!process.HasExited)
    {
        process.Kill(entireProcessTree: true);
        if (!process.WaitForExit(5_000))
        {
            throw new TimeoutException(
                $"SchedulerReplay target {process.Id} did not exit within 5 seconds.");
        }
    }

    AppendEvidence(evidencePath, new JsonObject
    {
        ["kind"] = "target-exit",
        ["session"] = session,
        ["pid"] = process.Id
    });
    process.Dispose();
}

static void AppendEvidence(string path, JsonObject evidence) =>
    File.AppendAllText(path, evidence.ToJsonString() + Environment.NewLine);

sealed class StopReleaseWorker : IDisposable
{
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(10);
    private readonly string _releasePath;
    private readonly Func<Task> _sendStopped;
    private readonly AdapterDiagnostics _diagnostics;
    private readonly int _session;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Thread _thread;

    public StopReleaseWorker(
        string releasePath,
        Func<Task> sendStopped,
        AdapterDiagnostics diagnostics,
        int session)
    {
        _releasePath = releasePath;
        _sendStopped = sendStopped;
        _diagnostics = diagnostics;
        _session = session;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"Replay stop release {session}"
        };
    }

    public void Start() => _thread.Start();

    public void Dispose()
    {
        _shutdown.Cancel();
        if (_thread.IsAlive && !_thread.Join(millisecondsTimeout: 2_000))
        {
            _diagnostics.RecordException(
                "stop-release-join-timeout",
                new TimeoutException("Stop release worker did not terminate within 2 seconds."));
        }

        _shutdown.Dispose();
    }

    private void Run()
    {
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(_releasePath))
            {
                if (_shutdown.Token.WaitHandle.WaitOne(millisecondsTimeout: 25))
                {
                    _diagnostics.Track("stop-release-cancelled", _session);
                    return;
                }

                if (deadline.Elapsed >= ReleaseTimeout)
                {
                    _diagnostics.RecordException(
                        "stop-release-timeout",
                        new TimeoutException(
                            $"Stop release signal was not created within {ReleaseTimeout}."));
                    return;
                }
            }

            if (_shutdown.IsCancellationRequested)
            {
                _diagnostics.Track("stop-release-cancelled", _session);
                return;
            }

            _diagnostics.Track("stop-release-observed", _session);
            _sendStopped().GetAwaiter().GetResult();
            _diagnostics.Track("stop-release-complete", _session);
        }
        catch (Exception ex)
        {
            _diagnostics.RecordException("stop-release-fault", ex);
        }
    }
}

sealed class AdapterDiagnostics
{
    private const uint SemNoGpFaultErrorBox = 0x0002;
    private readonly string _path;
    private readonly object _gate = new();
    private long _order;
    private int _session = -1;
    private string? _phase;
    private string? _command;

    public AdapterDiagnostics(string evidenceDirectory)
    {
        _path = Path.Combine(evidenceDirectory, "diagnostics.jsonl");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            RecordException(
                "appdomain-unhandled",
                args.ExceptionObject as Exception
                    ?? new InvalidOperationException(args.ExceptionObject?.ToString()),
                args.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            RecordException("unobserved-task", args.Exception);
            args.SetObserved();
        };
    }

    public static void SuppressWindowsErrorDialogs()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            SetErrorMode(SemNoGpFaultErrorBox);
        }
        catch
        {
            // Diagnostics must never become an adapter failure.
        }
    }

    public void Track(string phase, int session = -1, string? command = null)
    {
        lock (_gate)
        {
            _phase = phase;
            if (session >= 0)
            {
                _session = session;
            }

            if (command is not null)
            {
                _command = command;
            }

            Write("phase", exception: null, terminating: null);
        }
    }

    public void RecordException(
        string kind,
        Exception exception,
        bool? terminating = null)
    {
        lock (_gate)
        {
            Write(kind, exception, terminating);
        }
    }

    private void Write(string kind, Exception? exception, bool? terminating)
    {
        try
        {
            var record = new JsonObject
            {
                ["kind"] = kind,
                ["order"] = Interlocked.Increment(ref _order),
                ["session"] = _session,
                ["phase"] = _phase,
                ["command"] = _command
            };
            if (exception is not null)
            {
                record["exception"] = exception.ToString();
            }

            if (terminating is not null)
            {
                record["terminating"] = terminating.Value;
            }

            try
            {
                File.AppendAllText(_path, record.ToJsonString() + Environment.NewLine);
            }
            catch
            {
                // Diagnostics must never become an adapter failure.
            }

            if (exception is not null)
            {
                try
                {
                    Console.Error.WriteLine(record.ToJsonString());
                }
                catch
                {
                    // stderr may already be closed during transport teardown.
                }
            }
        }
        catch
        {
            // Diagnostics must never become an adapter failure.
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}
