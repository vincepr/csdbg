using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Csdbg.Core.Dap;

public sealed class DapClient : IDapClient
{
    private readonly string _netcoredbgPath;
    private readonly IDapProcessFactory _processFactory;
    private readonly TimeSpan _requestTimeout;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private IDapProcess? _process;
    private Task? _readLoop;
    private Task? _stderrLoop;
    private int _seq;
    private int _faulted;
    private int _disposed;

    public DapClient(string netcoredbgPath)
        : this(netcoredbgPath, new DapProcessFactory(), TimeSpan.FromSeconds(30))
    {
    }

    public DapClient(
        string netcoredbgPath,
        IDapProcessFactory processFactory,
        TimeSpan requestTimeout)
    {
        _netcoredbgPath = netcoredbgPath ?? throw new ArgumentNullException(nameof(netcoredbgPath));
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
        _requestTimeout = requestTimeout > TimeSpan.Zero
            ? requestTimeout
            : throw new ArgumentOutOfRangeException(nameof(requestTimeout));
    }

    public bool IsRunning =>
        Volatile.Read(ref _disposed) == 0
        && Volatile.Read(ref _faulted) == 0
        && _process is { HasExited: false };
    internal int PendingRequestCount => _pending.Count;
    public event Action<JsonObject>? EventReceived;
    public event Action<Exception>? Closed;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return;
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _process = _processFactory.Start(_netcoredbgPath);
        _readLoop = Task.Run(() => ReadLoopAsync(_lifetime.Token), CancellationToken.None);
        _stderrLoop = Task.Run(() => DrainStandardErrorAsync(_lifetime.Token), CancellationToken.None);

        try
        {
            await InitializeAsync(cancellationToken);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task<JsonObject> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync("initialize", new JsonObject
        {
            ["clientID"] = "csdbg",
            ["clientName"] = "csdbg",
            ["adapterID"] = "coreclr",
            ["pathFormat"] = "path",
            ["linesStartAt1"] = true,
            ["columnsStartAt1"] = true,
            ["supportsVariableType"] = true,
            ["supportsVariablePaging"] = true,
            ["supportsProgressReporting"] = true
        }, cancellationToken);
        if (response["success"]?.GetValue<bool>() != true)
        {
            throw new InvalidOperationException(
                response["message"]?.GetValue<string>() ?? "DAP adapter rejected initialization.");
        }

        return response;
    }

    public async Task<JsonObject> SendRequestAsync(
        string command,
        JsonObject? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsRunning || _process is null)
        {
            throw new InvalidOperationException("DAP client is not running.");
        }

        var seq = Interlocked.Increment(ref _seq);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[seq] = tcs;

        var request = new JsonObject
        {
            ["seq"] = seq,
            ["type"] = "request",
            ["command"] = command,
            ["arguments"] = arguments ?? new JsonObject()
        };

        try
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                if (!IsRunning)
                {
                    throw new EndOfStreamException("DAP adapter exited before the request could be written.");
                }

                try
                {
                    await DapMessageFraming.WriteAsync(
                        _process.StandardInput,
                        request,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    FaultTransport(ex);
                    throw;
                }
            }
            finally
            {
                _writeLock.Release();
            }

            await using var registration = cancellationToken.Register(
                () => tcs.TrySetCanceled(cancellationToken));
            return await tcs.Task.WaitAsync(_requestTimeout, cancellationToken);
        }
        finally
        {
            _pending.TryRemove(seq, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        FailPending(new ObjectDisposedException(nameof(DapClient)));

        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill();
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        try
        {
            if (_process is not null)
            {
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        catch
        {
            // Best-effort process cleanup.
        }

        await AwaitLoopAsync(_readLoop);
        await AwaitLoopAsync(_stderrLoop);
        _process?.Dispose();
        _writeLock.Dispose();
        _lifetime.Dispose();
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        if (_process?.StandardOutput is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await DapMessageFraming.ReadAsync(
                    _process.StandardOutput,
                    cancellationToken);
                if (message is null)
                {
                    throw new EndOfStreamException("DAP adapter closed its output stream.");
                }

                HandleMessage(message);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal disposal.
        }
        catch (Exception ex)
        {
            FaultTransport(ex);
        }
    }

    private async Task DrainStandardErrorAsync(CancellationToken cancellationToken)
    {
        if (_process is null)
        {
            return;
        }

        var buffer = new char[1024];
        try
        {
            while (await _process.StandardError.ReadAsync(buffer, cancellationToken) > 0)
            {
                // Draining prevents a full stderr pipe from blocking the adapter.
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal disposal.
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var item in _pending.ToArray())
        {
            if (_pending.TryRemove(item.Key, out var pending))
            {
                pending.TrySetException(exception);
            }
        }
    }

    private void FaultTransport(Exception exception)
    {
        if (Interlocked.Exchange(ref _faulted, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        FailPending(exception);
        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill();
            }
            catch
            {
                // Best-effort fault cleanup.
            }
        }

        Closed?.Invoke(exception);
    }

    private static async Task AwaitLoopAsync(Task? loop)
    {
        if (loop is null)
        {
            return;
        }

        try
        {
            await loop.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // The adapter process may already be gone.
        }
    }

    private void HandleMessage(JsonObject message)
    {
        var type = message["type"]?.GetValue<string>();
        if (type == "response")
        {
            var requestSeq = message["request_seq"]?.GetValue<int>() ?? 0;
            if (_pending.TryRemove(requestSeq, out var pending))
            {
                pending.TrySetResult(message);
            }
            return;
        }

        if (type == "event")
        {
            EventReceived?.Invoke(message);
        }
    }

}
