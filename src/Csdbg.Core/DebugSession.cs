using DapProtocolClient = Csdbg.Core.Dap.DapClient;

namespace Csdbg.Core;

public sealed class DebugSession : IAsyncDisposable
{
    private DapProtocolClient? _dapClient;
    private readonly List<BreakpointInfo> _breakpoints = [];

    public string State { get; private set; } = "idle";
    public string? StopReason { get; private set; }
    public int? CurrentThreadId { get; private set; }
    public BackendInfo Backend { get; private set; } = BackendLocator.FindNetcoredbg();

    public object GetStatus()
    {
        Backend = BackendLocator.FindNetcoredbg();

        return new
        {
            state = State,
            stopReason = StopReason,
            currentThreadId = CurrentThreadId,
            backend = new
            {
                Backend.Name,
                Backend.Path,
                Backend.Available,
                Backend.Error
            },
            breakpoints = _breakpoints.Select(item => new
            {
                item.Id,
                item.File,
                item.Line,
                item.Condition,
                item.Verified
            }).ToArray(),
            dapRunning = _dapClient?.IsRunning ?? false
        };
    }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        Backend = BackendLocator.FindNetcoredbg();
        if (!Backend.Available || Backend.Path is null)
        {
            throw new InvalidOperationException(Backend.Error);
        }

        if (_dapClient is not null && _dapClient.IsRunning)
        {
            return;
        }

        _dapClient = new DapProtocolClient(Backend.Path);
        _dapClient.EventReceived += OnDapEvent;
        State = "initializing";
        await _dapClient.StartAsync(cancellationToken);
    }

    public async Task<object> LaunchAsync(
        string program,
        string? cwd = null,
        IReadOnlyList<string>? args = null,
        bool stopAtEntry = false,
        CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        if (_dapClient is null)
        {
            throw new InvalidOperationException("DAP client is not running.");
        }

        foreach (var file in _breakpoints.Select(item => item.File).Distinct(StringComparer.Ordinal))
        {
            await SyncBreakpointsAsync(file, cancellationToken);
        }

        await _dapClient.SendRequestAsync("setExceptionBreakpoints", new System.Text.Json.Nodes.JsonObject
        {
            ["filters"] = new System.Text.Json.Nodes.JsonArray()
        }, cancellationToken);

        var arguments = new System.Text.Json.Nodes.JsonObject
        {
            ["program"] = Path.GetFullPath(program),
            ["cwd"] = cwd is null ? Path.GetDirectoryName(Path.GetFullPath(program)) : Path.GetFullPath(cwd),
            ["stopAtEntry"] = stopAtEntry,
            ["justMyCode"] = false,
            ["args"] = ToJsonArray(args ?? [])
        };

        var launchResponse = await _dapClient.SendRequestAsync("launch", arguments, cancellationToken);
        var success = launchResponse["success"]?.GetValue<bool>() == true;
        if (!success)
        {
            throw new InvalidOperationException(launchResponse["message"]?.GetValue<string>() ?? "DAP launch failed.");
        }

        await _dapClient.SendRequestAsync("configurationDone", cancellationToken: cancellationToken);
        State = "running";

        return GetStatus();
    }

    public async Task<object> AddBreakpointAsync(
        string file,
        int line,
        string? condition = null,
        CancellationToken cancellationToken = default)
    {
        var breakpoint = new BreakpointInfo
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            File = Path.GetFullPath(file),
            Line = line,
            Condition = condition
        };
        _breakpoints.Add(breakpoint);

        if (_dapClient is not null && _dapClient.IsRunning)
        {
            await SyncBreakpointsAsync(breakpoint.File, cancellationToken);
        }

        return breakpoint;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    public async Task<object> StopAsync()
    {
        if (_dapClient is not null)
        {
            await _dapClient.DisposeAsync();
            _dapClient = null;
        }

        State = "idle";
        StopReason = null;
        CurrentThreadId = null;
        return GetStatus();
    }

    private void OnDapEvent(System.Text.Json.Nodes.JsonObject message)
    {
        var eventName = message["event"]?.GetValue<string>();
        var body = message["body"]?.AsObject();

        switch (eventName)
        {
            case "initialized":
                State = "initialized";
                break;
            case "stopped":
                State = "stopped";
                StopReason = body?["reason"]?.GetValue<string>();
                CurrentThreadId = body?["threadId"]?.GetValue<int>();
                break;
            case "continued":
                State = "running";
                StopReason = null;
                break;
            case "terminated":
            case "exited":
                State = "terminated";
                break;
        }
    }

    private async Task SyncBreakpointsAsync(string file, CancellationToken cancellationToken)
    {
        if (_dapClient is null)
        {
            return;
        }

        var sourceBreakpoints = _breakpoints
            .Where(item => string.Equals(item.File, file, StringComparison.Ordinal))
            .OrderBy(item => item.Line)
            .ToList();

        var breakpointArray = new System.Text.Json.Nodes.JsonArray();
        foreach (var breakpoint in sourceBreakpoints)
        {
            var breakpointObject = new System.Text.Json.Nodes.JsonObject
            {
                ["line"] = breakpoint.Line
            };

            if (!string.IsNullOrWhiteSpace(breakpoint.Condition))
            {
                breakpointObject["condition"] = breakpoint.Condition;
            }

            breakpointArray.Add(breakpointObject);
        }

        var response = await _dapClient.SendRequestAsync("setBreakpoints", new System.Text.Json.Nodes.JsonObject
        {
            ["source"] = new System.Text.Json.Nodes.JsonObject
            {
                ["path"] = file
            },
            ["breakpoints"] = breakpointArray,
            ["sourceModified"] = false
        }, cancellationToken);

        var dapBreakpoints = response["body"]?["breakpoints"]?.AsArray();
        if (dapBreakpoints is null)
        {
            return;
        }

        for (var index = 0; index < dapBreakpoints.Count && index < sourceBreakpoints.Count; index++)
        {
            sourceBreakpoints[index].Verified = dapBreakpoints[index]?["verified"]?.GetValue<bool>() == true;
        }
    }

    private static System.Text.Json.Nodes.JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new System.Text.Json.Nodes.JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }
}
