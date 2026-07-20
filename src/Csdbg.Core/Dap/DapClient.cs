using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Csdbg.Core.Dap;

public sealed class DapClient : IAsyncDisposable
{
    private readonly string _netcoredbgPath;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Process? _process;
    private Task? _readLoop;
    private int _seq;

    public DapClient(string netcoredbgPath)
    {
        _netcoredbgPath = netcoredbgPath;
    }

    public bool IsRunning => _process is { HasExited: false };
    public event Action<JsonObject>? EventReceived;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            return;
        }

        var startInfo = new ProcessStartInfo(_netcoredbgPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--interpreter=vscode");

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start netcoredbg.");
        _readLoop = Task.Run(() => ReadLoopAsync(cancellationToken), cancellationToken);

        await InitializeAsync(cancellationToken);
    }

    public Task<JsonObject> InitializeAsync(CancellationToken cancellationToken = default)
    {
        return SendRequestAsync("initialize", new JsonObject
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
    }

    public async Task<JsonObject> SendRequestAsync(
        string command,
        JsonObject? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (_process?.StandardInput is null || _process.HasExited)
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

        var payload = JsonSerializer.SerializeToUtf8Bytes(request);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _process.StandardInput.BaseStream.WriteAsync(header, cancellationToken);
            await _process.StandardInput.BaseStream.WriteAsync(payload, cancellationToken);
            await _process.StandardInput.BaseStream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }

        await using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pending in _pending.Values)
        {
            pending.TrySetCanceled();
        }
        _pending.Clear();

        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // The process may already be gone.
            }
        }

        _process?.Dispose();
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        if (_process?.StandardOutput is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested && !_process.HasExited)
            {
                var contentLength = await ReadContentLengthAsync(_process.StandardOutput.BaseStream, cancellationToken);
                if (contentLength is null)
                {
                    break;
                }

                var payload = new byte[contentLength.Value];
                await _process.StandardOutput.BaseStream.ReadExactlyAsync(payload, cancellationToken);
                var message = JsonNode.Parse(payload)?.AsObject();
                if (message is null)
                {
                    continue;
                }

                HandleMessage(message);
            }
        }
        catch (Exception ex)
        {
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(ex);
            }
            _pending.Clear();
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

    private static async Task<int?> ReadContentLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        int? contentLength = null;

        while (true)
        {
            var line = await ReadAsciiLineAsync(stream, cancellationToken);
            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                return contentLength;
            }

            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["Content-Length:".Length..].Trim();
                contentLength = int.Parse(value);
            }
        }
    }

    private static async Task<string?> ReadAsciiLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var buffer = new byte[1];
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            }

            if (buffer[0] == (byte)'\n')
            {
                if (bytes.Count > 0 && bytes[^1] == (byte)'\r')
                {
                    bytes.RemoveAt(bytes.Count - 1);
                }
                return Encoding.ASCII.GetString(bytes.ToArray());
            }

            bytes.Add(buffer[0]);
        }
    }
}
