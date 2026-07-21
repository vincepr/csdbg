using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Csdbg.Core.Dap;

namespace Csdbg.Mcp.Tests;

internal sealed record ScriptedDapRequest(string Command, JsonObject? Arguments);

internal sealed class ScriptedDapClient : IDapClient
{
    private readonly Lock _gate = new();
    private readonly List<ScriptedDapRequest> _requests = [];
    private readonly Dictionary<string, TaskCompletionSource<bool>> _requestSignals = [];

    public bool IsRunning { get; private set; }
    public int CreateCount { get; set; }
    public Action<ScriptedDapClient>? OnStart { get; set; }
    public Func<ScriptedDapRequest, CancellationToken, Task<JsonObject>>? OnRequest { get; set; }

    public event Action<JsonObject>? EventReceived;
    public event Action<Exception>? Closed;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IsRunning = true;
        OnStart?.Invoke(this);
        return Task.CompletedTask;
    }

    public Task<JsonObject> SendRequestAsync(
        string command,
        JsonObject? arguments = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new ScriptedDapRequest(command, arguments?.DeepClone().AsObject());
        TaskCompletionSource<bool>? signal;
        lock (_gate)
        {
            _requests.Add(request);
            _requestSignals.Remove(command, out signal);
        }

        signal?.TrySetResult(true);
        return OnRequest?.Invoke(request, cancellationToken)
            ?? Task.FromResult(Success(command));
    }

    public async Task WaitForRequestAsync(string command, TimeSpan timeout)
    {
        Task waitTask;
        lock (_gate)
        {
            if (_requests.Any(request => request.Command == command))
            {
                return;
            }

            if (!_requestSignals.TryGetValue(command, out var signal))
            {
                signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _requestSignals.Add(command, signal);
            }

            waitTask = signal.Task;
        }

        await waitTask.WaitAsync(timeout);
    }

    public void EmitInitialized() => Emit("initialized");

    public void EmitStopped(string reason, int threadId = 1) =>
        Emit("stopped", new JsonObject
        {
            ["reason"] = reason,
            ["threadId"] = threadId,
            ["allThreadsStopped"] = true
        });

    public void EmitClosed(Exception exception) => Closed?.Invoke(exception);

    public ValueTask DisposeAsync()
    {
        IsRunning = false;
        return ValueTask.CompletedTask;
    }

    public static JsonObject Success(string command, JsonObject? body = null) =>
        new()
        {
            ["type"] = "response",
            ["command"] = command,
            ["success"] = true,
            ["body"] = body ?? new JsonObject()
        };

    private void Emit(string eventName, JsonObject? body = null) =>
        EventReceived?.Invoke(new JsonObject
        {
            ["type"] = "event",
            ["event"] = eventName,
            ["body"] = body ?? new JsonObject()
        });
}

internal sealed class ScriptedDapClientFactory(ScriptedDapClient client) : IDapClientFactory
{
    public IDapClient Create(string netcoredbgPath)
    {
        client.CreateCount++;
        return client;
    }
}

internal sealed class TestLineReader : TextReader
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public void WriteLine(string line) =>
        Assert.True(_lines.Writer.TryWrite(line), "The MCP input has already completed.");

    public void Complete() => _lines.Writer.TryComplete();

    public override async Task<string?> ReadLineAsync()
    {
        while (await _lines.Reader.WaitToReadAsync())
        {
            if (_lines.Reader.TryRead(out var line))
            {
                return line;
            }
        }

        return null;
    }
}

internal sealed class TestLineWriter : TextWriter
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

    public override Encoding Encoding => Encoding.UTF8;

    public override Task WriteLineAsync(string? value)
    {
        Assert.NotNull(value);
        Assert.True(_lines.Writer.TryWrite(value));
        return Task.CompletedTask;
    }

    public async Task<string> ReadLineAsync(TimeSpan timeout) =>
        await _lines.Reader.ReadAsync().AsTask().WaitAsync(timeout);
}
