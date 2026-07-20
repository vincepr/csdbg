using System.Text.Json.Nodes;
using Csdbg.Core.Dap;

namespace Csdbg.Core;

public sealed class DebugSession : IAsyncDisposable
{
    private static readonly TimeSpan DefaultExecutionTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<BackendInfo> _backendResolver;
    private readonly IDapClientFactory _dapClientFactory;
    private IDapClient? _dapClient;
    private readonly List<BreakpointInfo> _breakpoints = [];
    private readonly SemaphoreSlim _breakpointOperationLock = new(1, 1);
    private readonly Lock _gate = new();
    private TaskCompletionSource<bool> _stateChanged = CreateSignal();
    private int _stateVersion;
    private int? _exitCode;
    private string? _currentSourcePath;
    private int? _currentSourceLine;
    private string? _currentFrameName;
    private readonly List<string> _recentOutput = [];
    private readonly HashSet<int> _knownThreadIds = [];
    private int _resumeCommandActive;
    private int _launchCommandActive;

    public DebugSession()
        : this(BackendLocator.FindNetcoredbg, new DapClientFactory())
    {
    }

    public DebugSession(Func<BackendInfo> backendResolver, IDapClientFactory dapClientFactory)
    {
        _backendResolver = backendResolver ?? throw new ArgumentNullException(nameof(backendResolver));
        _dapClientFactory = dapClientFactory ?? throw new ArgumentNullException(nameof(dapClientFactory));
        Backend = _backendResolver();
    }

    public string State { get; private set; } = "idle";
    public string? StopReason { get; private set; }
    public int? CurrentThreadId { get; private set; }
    public BackendInfo Backend { get; private set; }

    public object GetStatus()
    {
        Backend = _backendResolver();
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
                    RequestedLine = item.RequestedLine,
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
                item.RequestedLine,
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
        Backend = _backendResolver();
        if (!Backend.Available || Backend.Path is null)
        {
            throw new InvalidOperationException(Backend.Error);
        }

        if (_dapClient is not null && _dapClient.IsRunning)
        {
            return;
        }

        _dapClient = _dapClientFactory.Create(Backend.Path);
        _dapClient.EventReceived += OnDapEvent;
        _dapClient.Closed += OnDapClosed;
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
        if (Interlocked.CompareExchange(ref _launchCommandActive, 1, 0) != 0)
        {
            throw new InvalidOperationException("A launch command is already in progress.");
        }

        try
        {
            if (State != "idle")
            {
                throw new InvalidOperationException("start_debug requires an idle debugger session.");
            }

            await EnsureStartedAsync(cancellationToken);
            if (_dapClient is null)
            {
                throw new InvalidOperationException("DAP client is not running.");
            }

            var arguments = new JsonObject
            {
                ["program"] = Path.GetFullPath(program),
                ["cwd"] = cwd is null ? Path.GetDirectoryName(Path.GetFullPath(program)) : Path.GetFullPath(cwd),
                ["stopAtEntry"] = stopAtEntry,
                ["justMyCode"] = false,
                ["args"] = ToJsonArray(args ?? [])
            };

            var launchTask = SendCheckedRequestAsync("launch", arguments, cancellationToken);
            await WaitForStateAsync(
                "initialized",
                DefaultExecutionTimeout,
                cancellationToken);

            await _breakpointOperationLock.WaitAsync(cancellationToken);
            try
            {
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
            }
            finally
            {
                _breakpointOperationLock.Release();
            }

            await SendCheckedRequestAsync("setExceptionBreakpoints", new JsonObject
            {
                ["filters"] = new JsonArray()
            }, cancellationToken);

            var executionVersion = GetStateVersion();
            await SendCheckedRequestAsync("configurationDone", cancellationToken: cancellationToken);
            await launchTask;
            if (State is not "stopped" and not "terminated")
            {
                SetState("running");
            }

            if (stopAtEntry)
            {
                return await WaitForExecutionStateAsync(
                    executionVersion,
                    DefaultExecutionTimeout,
                    cancellationToken);
            }

            return GetStatus();
        }
        finally
        {
            Volatile.Write(ref _launchCommandActive, 0);
        }
    }

    public async Task<object> AddBreakpointAsync(
        string file,
        int line,
        string? condition = null,
        CancellationToken cancellationToken = default)
    {
        await _breakpointOperationLock.WaitAsync(cancellationToken);
        var breakpoint = new BreakpointInfo
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            File = Path.GetFullPath(file),
            RequestedLine = line,
            Line = line,
            Condition = condition
        };

        try
        {
            lock (_gate)
            {
                _breakpoints.Add(breakpoint);
            }

            try
            {
                if (_dapClient is not null && _dapClient.IsRunning)
                {
                    await SyncBreakpointsAsync(breakpoint.File, cancellationToken);
                }

                return breakpoint;
            }
            catch
            {
                lock (_gate)
                {
                    _breakpoints.Remove(breakpoint);
                }

                throw;
            }
        }
        finally
        {
            _breakpointOperationLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    public async Task<object> ContinueAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        return await ResumeAsync("continue", "continue", timeout, cancellationToken);
    }

    public async Task<object> StepOverAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        return await ResumeAsync("step_over", "next", timeout, cancellationToken);
    }

    public async Task<object> StepIntoAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        return await ResumeAsync("step_into", "stepIn", timeout, cancellationToken);
    }

    public async Task<object> StepOutAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        return await ResumeAsync("step_out", "stepOut", timeout, cancellationToken);
    }

    public async Task<object> PauseAsync(
        int? threadId = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        EnsureDebuggeeActive();
        RequireRunningState("pause_execution");
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
        await _breakpointOperationLock.WaitAsync(cancellationToken);
        try
        {
            BreakpointInfo? removed;
            var removedIndex = -1;
            lock (_gate)
            {
                removedIndex = _breakpoints.FindIndex(item => string.Equals(item.Id, id, StringComparison.Ordinal));
                removed = removedIndex >= 0 ? _breakpoints[removedIndex] : null;
                if (removed is not null)
                {
                    _breakpoints.RemoveAt(removedIndex);
                }
            }

            if (removed is null)
            {
                throw new InvalidOperationException($"Breakpoint not found: {id}");
            }

            try
            {
                if (_dapClient is not null && _dapClient.IsRunning && State is not "idle" and not "terminated")
                {
                    await SyncBreakpointsAsync(removed.File, cancellationToken);
                }
            }
            catch
            {
                lock (_gate)
                {
                    _breakpoints.Insert(removedIndex, removed);
                }

                throw;
            }

            return new
            {
                removed = new
                {
                    removed.Id,
                    removed.File,
                    removed.RequestedLine,
                    removed.Line,
                    removed.Condition,
                    removed.Verified,
                    removed.AdapterId,
                    removed.Message
                },
                status = GetStatus()
            };
        }
        finally
        {
            _breakpointOperationLock.Release();
        }
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

    private void OnDapClosed(Exception exception)
    {
        if (State is "idle" or "terminated")
        {
            return;
        }

        RecordOutput("adapter", exception.Message);
        SetState("terminated");
        NotifyStateChanged();
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
                .OrderBy(item => item.RequestedLine)
                .ToList();
        }

        var breakpointArray = new System.Text.Json.Nodes.JsonArray();
        foreach (var breakpoint in sourceBreakpoints)
        {
            var breakpointObject = new System.Text.Json.Nodes.JsonObject
            {
                ["line"] = breakpoint.RequestedLine
            };

            if (!string.IsNullOrWhiteSpace(breakpoint.Condition))
            {
                breakpointObject["condition"] = breakpoint.Condition;
            }

            breakpointArray.Add(breakpointObject);
        }

        var response = await SendCheckedRequestAsync("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject
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
                var transitioned = snapshot.Version > startingVersion;
                if (transitioned && snapshot.State == "stopped")
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

                if (transitioned && snapshot.State is ("terminated" or "idle"))
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

    private async Task WaitForStateAsync(
        string expectedState,
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
                if (snapshot.State == expectedState)
                {
                    return;
                }

                if (snapshot.State == "terminated")
                {
                    throw new InvalidOperationException(
                        $"Debugger terminated while waiting for state '{expectedState}'.");
                }

                await WaitForStateChangeAsync(snapshot.Version, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for debugger state '{expectedState}'.");
        }
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

    private async Task<object> ResumeAsync(
        string operationName,
        string dapCommand,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        EnsureDebuggeeActive();
        var threadId = RequireStoppedThreadId(operationName);
        if (Interlocked.CompareExchange(ref _resumeCommandActive, 1, 0) != 0)
        {
            throw new InvalidOperationException("Another continue or step command is already in progress.");
        }

        try
        {
            var stateVersion = GetStateVersion();
            await SendCheckedRequestAsync(dapCommand, new JsonObject
            {
                ["threadId"] = threadId
            }, cancellationToken);

            return await WaitForExecutionStateAsync(
                stateVersion,
                timeout ?? DefaultExecutionTimeout,
                cancellationToken);
        }
        finally
        {
            Volatile.Write(ref _resumeCommandActive, 0);
        }
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

    private void RequireRunningState(string operationName)
    {
        if (State != "running")
        {
            throw new InvalidOperationException($"{operationName} requires the debuggee to be running.");
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
                (item.Line == line.Value || item.RequestedLine == line.Value) &&
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
                (item.Line == _currentSourceLine.Value || item.RequestedLine == _currentSourceLine.Value) &&
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
