using System.Text.Json.Nodes;
using DapProtocolClient = Csdbg.Core.Dap.DapClient;

namespace Csdbg.Core;

public sealed class DebugSession : IAsyncDisposable
{
    private static readonly TimeSpan DefaultExecutionTimeout = TimeSpan.FromSeconds(30);

    private DapProtocolClient? _dapClient;
    private readonly List<BreakpointInfo> _breakpoints = [];
    private readonly Lock _gate = new();
    private TaskCompletionSource<bool> _stateChanged = CreateSignal();
    private int _stateVersion;
    private int? _exitCode;
    private string? _currentSourcePath;
    private int? _currentSourceLine;
    private string? _currentFrameName;
    private readonly List<string> _recentOutput = [];
    private readonly HashSet<int> _knownThreadIds = [];

    public string State { get; private set; } = "idle";
    public string? StopReason { get; private set; }
    public int? CurrentThreadId { get; private set; }
    public BackendInfo Backend { get; private set; } = BackendLocator.FindNetcoredbg();

    public object GetStatus()
    {
        Backend = BackendLocator.FindNetcoredbg();
        BreakpointInfo[] breakpoints;
        string[] recentOutput;
        int[] knownThreadIds;
        string? currentSourcePath;
        int? currentSourceLine;
        string? currentFrameName;
        int? exitCode;

        lock (_gate)
        {
            breakpoints = _breakpoints
                .Select(item => new BreakpointInfo
                {
                    Id = item.Id,
                    File = item.File,
                    Line = item.Line,
                    Condition = item.Condition,
                    Verified = item.Verified,
                    AdapterId = item.AdapterId,
                    Message = item.Message
                })
                .ToArray();
            recentOutput = _recentOutput.ToArray();
            knownThreadIds = _knownThreadIds.OrderBy(item => item).ToArray();
            currentSourcePath = _currentSourcePath;
            currentSourceLine = _currentSourceLine;
            currentFrameName = _currentFrameName;
            exitCode = _exitCode;
        }

        return new
        {
            state = State,
            stopReason = StopReason,
            currentThreadId = CurrentThreadId,
            exitCode,
            currentLocation = currentSourcePath is null ? null : new
            {
                file = currentSourcePath,
                line = currentSourceLine,
                frame = currentFrameName
            },
            recentOutput,
            knownThreadIds,
            backend = new
            {
                Backend.Name,
                Backend.Path,
                Backend.Available,
                Backend.Error
            },
            breakpoints = breakpoints.Select(item => new
            {
                item.Id,
                item.File,
                item.Line,
                item.Condition,
                item.Verified,
                item.AdapterId,
                item.Message
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
        SetState("initializing");
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

        string[] breakpointFiles;
        lock (_gate)
        {
            breakpointFiles = _breakpoints
                .Select(item => item.File)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        foreach (var file in breakpointFiles)
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

        await SendCheckedRequestAsync("launch", arguments, cancellationToken);
        await SendCheckedRequestAsync("configurationDone", cancellationToken: cancellationToken);
        SetState("running");

        if (stopAtEntry)
        {
            return await WaitForExecutionStateAsync(GetStateVersion(), DefaultExecutionTimeout, cancellationToken);
        }

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
        lock (_gate)
        {
            _breakpoints.Add(breakpoint);
        }

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

    public async Task<object> ContinueAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        var threadId = RequireStoppedThreadId("continue");
        var stateVersion = GetStateVersion();

        await SendCheckedRequestAsync("continue", new JsonObject
        {
            ["threadId"] = threadId
        }, cancellationToken);

        return await WaitForExecutionStateAsync(stateVersion, timeout ?? DefaultExecutionTimeout, cancellationToken);
    }

    public async Task<object> StepOverAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        var threadId = RequireStoppedThreadId("step_over");
        var stateVersion = GetStateVersion();

        await SendCheckedRequestAsync("next", new JsonObject
        {
            ["threadId"] = threadId
        }, cancellationToken);

        return await WaitForExecutionStateAsync(stateVersion, timeout ?? DefaultExecutionTimeout, cancellationToken);
    }

    public async Task<object> StepIntoAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        var threadId = RequireStoppedThreadId("step_into");
        var stateVersion = GetStateVersion();

        await SendCheckedRequestAsync("stepIn", new JsonObject
        {
            ["threadId"] = threadId
        }, cancellationToken);

        return await WaitForExecutionStateAsync(stateVersion, timeout ?? DefaultExecutionTimeout, cancellationToken);
    }

    public async Task<object> StepOutAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        var threadId = RequireStoppedThreadId("step_out");
        var stateVersion = GetStateVersion();

        await SendCheckedRequestAsync("stepOut", new JsonObject
        {
            ["threadId"] = threadId
        }, cancellationToken);

        return await WaitForExecutionStateAsync(stateVersion, timeout ?? DefaultExecutionTimeout, cancellationToken);
    }

    public async Task<object> PauseAsync(
        int? threadId = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        EnsureDebuggeeActive();
        var resolvedThreadId = threadId ?? CurrentThreadId ?? GetFirstKnownThreadId()
            ?? throw new InvalidOperationException("pause_execution requires a thread id before any thread is known.");
        var stateVersion = GetStateVersion();

        await SendCheckedRequestAsync("pause", new JsonObject
        {
            ["threadId"] = resolvedThreadId
        }, cancellationToken);

        return await WaitForExecutionStateAsync(stateVersion, timeout ?? DefaultExecutionTimeout, cancellationToken);
    }

    public async Task<object> RemoveBreakpointAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        BreakpointInfo? removed;
        lock (_gate)
        {
            removed = _breakpoints.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            if (removed is not null)
            {
                _breakpoints.Remove(removed);
            }
        }

        if (removed is null)
        {
            throw new InvalidOperationException($"Breakpoint not found: {id}");
        }

        if (_dapClient is not null && _dapClient.IsRunning && State is not "idle" and not "terminated")
        {
            await SyncBreakpointsAsync(removed.File, cancellationToken);
        }

        return new
        {
            removed = new
            {
                removed.Id,
                removed.File,
                removed.Line,
                removed.Condition,
                removed.Verified,
                removed.AdapterId,
                removed.Message
            },
            status = GetStatus()
        };
    }

    public async Task<object> GetThreadsAsync(CancellationToken cancellationToken = default)
    {
        EnsureDebuggeeActive();
        var response = await SendCheckedRequestAsync("threads", cancellationToken: cancellationToken);
        return new
        {
            state = State,
            currentThreadId = CurrentThreadId,
            threads = response["body"]?["threads"]?.DeepClone() ?? new JsonArray()
        };
    }

    public async Task<object> GetCallStackAsync(
        int? threadId = null,
        int startFrame = 0,
        int levels = 20,
        CancellationToken cancellationToken = default)
    {
        var resolvedThreadId = threadId ?? RequireStoppedThreadId("get_call_stack");
        RequireStoppedState("get_call_stack");

        var response = await SendCheckedRequestAsync("stackTrace", new JsonObject
        {
            ["threadId"] = resolvedThreadId,
            ["startFrame"] = Math.Max(0, startFrame),
            ["levels"] = Math.Max(1, levels)
        }, cancellationToken);

        return new
        {
            state = State,
            threadId = resolvedThreadId,
            totalFrames = response["body"]?["totalFrames"]?.GetValue<int?>(),
            stackFrames = response["body"]?["stackFrames"]?.DeepClone() ?? new JsonArray()
        };
    }

    public async Task<object> GetScopesAsync(int frameId, CancellationToken cancellationToken = default)
    {
        RequireStoppedState("get_scopes");

        var response = await SendCheckedRequestAsync("scopes", new JsonObject
        {
            ["frameId"] = frameId
        }, cancellationToken);

        return new
        {
            state = State,
            frameId,
            scopes = response["body"]?["scopes"]?.DeepClone() ?? new JsonArray()
        };
    }

    public async Task<object> GetVariablesAsync(
        int variablesReference,
        int? start = null,
        int? count = null,
        CancellationToken cancellationToken = default)
    {
        RequireStoppedState("get_variables");

        var arguments = new JsonObject
        {
            ["variablesReference"] = variablesReference
        };

        if (start is >= 0)
        {
            arguments["start"] = start.Value;
        }

        if (count is > 0)
        {
            arguments["count"] = count.Value;
        }

        var response = await SendCheckedRequestAsync("variables", arguments, cancellationToken);

        return new
        {
            state = State,
            variablesReference,
            variables = response["body"]?["variables"]?.DeepClone() ?? new JsonArray()
        };
    }

    public async Task<object> EvaluateExpressionAsync(
        string expression,
        int? frameId = null,
        string? context = null,
        bool allowUnsafe = false,
        CancellationToken cancellationToken = default)
    {
        RequireStoppedState("evaluate_expression");
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new InvalidOperationException("evaluate_expression requires a non-empty expression.");
        }

        var risk = EvaluationSafetyPolicy.Classify(expression);
        if (!allowUnsafe && risk.RequiresUnsafe)
        {
            throw new InvalidOperationException(
                $"Expression rejected by default safety policy: {risk.Reason}. Pass unsafe=true to evaluate it anyway.");
        }

        var arguments = new JsonObject
        {
            ["expression"] = expression,
            ["context"] = string.IsNullOrWhiteSpace(context) ? "watch" : context
        };

        if (frameId is not null)
        {
            arguments["frameId"] = frameId.Value;
        }

        var response = await SendCheckedRequestAsync("evaluate", arguments, cancellationToken);
        var body = response["body"]?.AsObject() ?? new JsonObject();

        return new
        {
            state = State,
            expression,
            context = arguments["context"]?.GetValue<string>(),
            unsafeAllowed = allowUnsafe,
            risk = new
            {
                requiresUnsafe = risk.RequiresUnsafe,
                risk.Reason
            },
            result = body["result"]?.GetValue<string>(),
            type = body["type"]?.GetValue<string>(),
            variablesReference = body["variablesReference"]?.GetValue<int?>(),
            presentationHint = body["presentationHint"]?.DeepClone()
        };
    }

    public async Task<object> StopAsync()
    {
        if (_dapClient is not null)
        {
            await _dapClient.DisposeAsync();
            _dapClient = null;
        }

        SetState("idle");
        StopReason = null;
        CurrentThreadId = null;
        lock (_gate)
        {
            _exitCode = null;
            _currentSourcePath = null;
            _currentSourceLine = null;
            _currentFrameName = null;
            _recentOutput.Clear();
            _knownThreadIds.Clear();
        }
        NotifyStateChanged();
        return GetStatus();
    }

    private void OnDapEvent(JsonObject message)
    {
        var eventName = message["event"]?.GetValue<string>();
        var body = message["body"]?.AsObject();

        switch (eventName)
        {
            case "initialized":
                SetState("initialized");
                NotifyStateChanged();
                break;
            case "stopped":
                SetState("stopped");
                StopReason = body?["reason"]?.GetValue<string>();
                CurrentThreadId = body?["threadId"]?.GetValue<int>();
                if (CurrentThreadId is { } stoppedThreadId)
                {
                    lock (_gate)
                    {
                        _knownThreadIds.Add(stoppedThreadId);
                    }
                }
                NotifyStateChanged();
                break;
            case "continued":
                SetState("running");
                StopReason = null;
                ClearCurrentLocation();
                NotifyStateChanged();
                break;
            case "output":
                RecordOutput(body?["category"]?.GetValue<string>(), body?["output"]?.GetValue<string>());
                break;
            case "thread":
                UpdateThreadSet(body);
                break;
            case "breakpoint":
                UpdateBreakpoint(body);
                break;
            case "terminated":
                SetState("terminated");
                NotifyStateChanged();
                break;
            case "exited":
                SetState("terminated");
                lock (_gate)
                {
                    _exitCode = body?["exitCode"]?.GetValue<int>();
                }
                NotifyStateChanged();
                break;
        }
    }

    private async Task SyncBreakpointsAsync(string file, CancellationToken cancellationToken)
    {
        if (_dapClient is null)
        {
            return;
        }

        List<BreakpointInfo> sourceBreakpoints;
        lock (_gate)
        {
            sourceBreakpoints = _breakpoints
                .Where(item => string.Equals(item.File, file, StringComparison.Ordinal))
                .OrderBy(item => item.Line)
                .ToList();
        }

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
            var dapBreakpoint = dapBreakpoints[index]?.AsObject();
            var verified = dapBreakpoint?["verified"]?.GetValue<bool>() == true;
            var adapterId = dapBreakpoint?["id"]?.GetValue<int?>();
            var message = dapBreakpoint?["message"]?.GetValue<string>();
            var resolvedLine = dapBreakpoint?["line"]?.GetValue<int?>();
            lock (_gate)
            {
                sourceBreakpoints[index].Verified = verified;
                sourceBreakpoints[index].AdapterId = adapterId;
                sourceBreakpoints[index].Message = message;
                if (resolvedLine is > 0)
                {
                    sourceBreakpoints[index].Line = resolvedLine.Value;
                }
            }
        }
    }

    private async Task<object> WaitForExecutionStateAsync(
        int startingVersion,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            while (true)
            {
                var snapshot = GetSnapshot();
                if (snapshot.State == "stopped")
                {
                    await RefreshCurrentLocationAsync(timeoutCts.Token);
                    MarkCurrentLocationBreakpointVerified();
                    return new
                    {
                        timedOut = false,
                        status = GetStatus(),
                        nextActions = new[] { "get_call_stack", "get_scopes", "get_variables", "step_over", "continue_execution" }
                    };
                }

                if (snapshot.State is "terminated" or "idle")
                {
                    return new
                    {
                        timedOut = false,
                        status = GetStatus(),
                        nextActions = Array.Empty<string>()
                    };
                }

                await WaitForStateChangeAsync(snapshot.Version, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new
            {
                timedOut = true,
                status = GetStatus(),
                nextActions = new[] { "get_status", "stop_debug" }
            };
        }
    }

    private async Task WaitForStateChangeAsync(int observedVersion, CancellationToken cancellationToken)
    {
        Task waitTask;
        lock (_gate)
        {
            if (_stateVersion != observedVersion)
            {
                return;
            }

            waitTask = _stateChanged.Task;
        }

        await waitTask.WaitAsync(cancellationToken);
    }

    private async Task RefreshCurrentLocationAsync(CancellationToken cancellationToken)
    {
        if (_dapClient is null || State != "stopped" || CurrentThreadId is null)
        {
            return;
        }

        var response = await SendCheckedRequestAsync("stackTrace", new JsonObject
        {
            ["threadId"] = CurrentThreadId.Value,
            ["startFrame"] = 0,
            ["levels"] = 1
        }, cancellationToken);

        var frame = response["body"]?["stackFrames"]?.AsArray()?.FirstOrDefault()?.AsObject();
        if (frame is null)
        {
            return;
        }

        lock (_gate)
        {
            _currentFrameName = frame["name"]?.GetValue<string>();
            _currentSourcePath = frame["source"]?["path"]?.GetValue<string>();
            _currentSourceLine = frame["line"]?.GetValue<int>();
        }
    }

    private async Task<JsonObject> SendCheckedRequestAsync(
        string command,
        JsonObject? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (_dapClient is null)
        {
            throw new InvalidOperationException("DAP client is not running.");
        }

        var response = await _dapClient.SendRequestAsync(command, arguments, cancellationToken);
        if (response["success"]?.GetValue<bool>() == true)
        {
            return response;
        }

        throw new InvalidOperationException(response["message"]?.GetValue<string>() ?? $"DAP request failed: {command}");
    }

    private int RequireStoppedThreadId(string operationName)
    {
        if (State != "stopped" || CurrentThreadId is null)
        {
            throw new InvalidOperationException($"{operationName} requires the debuggee to be stopped.");
        }

        return CurrentThreadId.Value;
    }

    private int? GetFirstKnownThreadId()
    {
        lock (_gate)
        {
            return _knownThreadIds
                .OrderBy(item => item)
                .Select<int, int?>(item => item)
                .FirstOrDefault();
        }
    }

    private void RequireStoppedState(string operationName)
    {
        if (State != "stopped")
        {
            throw new InvalidOperationException($"{operationName} requires the debuggee to be stopped.");
        }
    }

    private void EnsureDebuggeeActive()
    {
        if (_dapClient is null || !_dapClient.IsRunning || State is "idle" or "terminated")
        {
            throw new InvalidOperationException("Debuggee is not active.");
        }
    }

    private void RecordOutput(string? category, string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        var lines = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => string.IsNullOrWhiteSpace(category) ? line : $"[{category}] {line}");

        lock (_gate)
        {
            _recentOutput.AddRange(lines);
            if (_recentOutput.Count > 20)
            {
                _recentOutput.RemoveRange(0, _recentOutput.Count - 20);
            }
        }
    }

    private void UpdateThreadSet(JsonObject? body)
    {
        var reason = body?["reason"]?.GetValue<string>();
        var threadId = body?["threadId"]?.GetValue<int>();
        if (threadId is null)
        {
            return;
        }

        lock (_gate)
        {
            if (reason == "exited")
            {
                _knownThreadIds.Remove(threadId.Value);
            }
            else
            {
                _knownThreadIds.Add(threadId.Value);
            }
        }
    }

    private void UpdateBreakpoint(JsonObject? body)
    {
        var dapBreakpoint = body?["breakpoint"]?.AsObject();
        if (dapBreakpoint is null)
        {
            return;
        }

        var adapterId = dapBreakpoint["id"]?.GetValue<int?>();
        var verified = dapBreakpoint["verified"]?.GetValue<bool>() == true;
        var message = dapBreakpoint["message"]?.GetValue<string>();
        var line = dapBreakpoint["line"]?.GetValue<int?>();
        var sourcePath = dapBreakpoint["source"]?["path"]?.GetValue<string>();

        lock (_gate)
        {
            var match = _breakpoints.FirstOrDefault(item =>
                adapterId is not null && item.AdapterId == adapterId.Value);

            match ??= _breakpoints.FirstOrDefault(item =>
                line is not null &&
                item.Line == line.Value &&
                sourcePath is not null &&
                string.Equals(item.File, sourcePath, StringComparison.Ordinal));

            if (match is null)
            {
                return;
            }

            match.Verified = verified;
            match.Message = message;
            match.AdapterId = adapterId ?? match.AdapterId;
            if (line is > 0)
            {
                match.Line = line.Value;
            }
        }
    }

    private void MarkCurrentLocationBreakpointVerified()
    {
        if (StopReason != "breakpoint")
        {
            return;
        }

        lock (_gate)
        {
            if (_currentSourcePath is null || _currentSourceLine is null)
            {
                return;
            }

            var match = _breakpoints.FirstOrDefault(item =>
                item.Line == _currentSourceLine.Value &&
                string.Equals(item.File, _currentSourcePath, StringComparison.Ordinal));

            if (match is not null)
            {
                match.Verified = true;
            }
        }
    }

    private void ClearCurrentLocation()
    {
        lock (_gate)
        {
            _currentSourcePath = null;
            _currentSourceLine = null;
            _currentFrameName = null;
        }
    }

    private void SetState(string value)
    {
        State = value;
    }

    private int GetStateVersion()
    {
        lock (_gate)
        {
            return _stateVersion;
        }
    }

    private (string State, int Version) GetSnapshot()
    {
        lock (_gate)
        {
            return (State, _stateVersion);
        }
    }

    private void NotifyStateChanged()
    {
        TaskCompletionSource<bool> signal;
        lock (_gate)
        {
            _stateVersion++;
            signal = _stateChanged;
            _stateChanged = CreateSignal();
        }

        signal.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> CreateSignal()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
