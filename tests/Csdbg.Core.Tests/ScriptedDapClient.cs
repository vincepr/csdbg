using System.Text.Json.Nodes;
using Csdbg.Core.Dap;

namespace Csdbg.Core.Tests;

internal sealed record ScriptedDapRequest(string Command, JsonObject? Arguments);

internal sealed class ScriptedDapClient : IDapClient
{
    private readonly Lock _gate = new();
    private readonly List<ScriptedDapRequest> _requests = [];
    private readonly Dictionary<string, TaskCompletionSource<bool>> _requestSignals = [];

    public bool IsRunning { get; private set; }
    public int StartCount { get; private set; }
    public int DisposeCount { get; private set; }
    public Action<ScriptedDapClient>? OnStart { get; set; }
    public Func<ScriptedDapRequest, CancellationToken, Task<JsonObject>>? OnRequest { get; set; }

    public event Action<JsonObject>? EventReceived;
    public event Action<Exception>? Closed;

    public IReadOnlyList<ScriptedDapRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCount++;
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

        var request = new ScriptedDapRequest(
            command,
            arguments?.DeepClone().AsObject());
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

    public async Task WaitForRequestAsync(
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
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

        await waitTask.WaitAsync(timeout, cancellationToken);
    }

    public int RequestCount(string command)
    {
        lock (_gate)
        {
            return _requests.Count(request => request.Command == command);
        }
    }

    public void EmitInitialized()
    {
        Emit("initialized");
    }

    public void EmitStopped(int threadId = 1, string reason = "breakpoint")
    {
        Emit("stopped", new JsonObject
        {
            ["reason"] = reason,
            ["threadId"] = threadId,
            ["allThreadsStopped"] = true
        });
    }

    public void EmitContinued(int threadId = 1)
    {
        Emit("continued", new JsonObject
        {
            ["threadId"] = threadId,
            ["allThreadsContinued"] = true
        });
    }

    public void EmitClosed(Exception exception)
    {
        Closed?.Invoke(exception);
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        IsRunning = false;
        return ValueTask.CompletedTask;
    }

    public static JsonObject Success(string command, JsonObject? body = null)
    {
        return new JsonObject
        {
            ["type"] = "response",
            ["command"] = command,
            ["success"] = true,
            ["body"] = body ?? new JsonObject()
        };
    }

    private void Emit(string eventName, JsonObject? body = null)
    {
        EventReceived?.Invoke(new JsonObject
        {
            ["type"] = "event",
            ["event"] = eventName,
            ["body"] = body ?? new JsonObject()
        });
    }
}

internal sealed class ScriptedDapClientFactory(ScriptedDapClient client) : IDapClientFactory
{
    public int CreateCount { get; private set; }
    public string? LastPath { get; private set; }

    public IDapClient Create(string netcoredbgPath)
    {
        CreateCount++;
        LastPath = netcoredbgPath;
        return client;
    }
}
