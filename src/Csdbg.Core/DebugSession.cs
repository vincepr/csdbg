using System.Text.Json.Nodes;
using Csdbg.Core.Dap;

namespace Csdbg.Core;

public sealed class DebugSession : IAsyncDisposable
{
    private static readonly TimeSpan DefaultExecutionTimeout = TimeSpan.FromSeconds(30);
    private static readonly StringComparer SourcePathComparer = SourcePathIdentity.CurrentComparer;

    private readonly Func<BackendInfo> _backendResolver;
    private readonly IDapClientFactory _dapClientFactory;
    private IDapClient? _dapClient;
    private readonly List<BreakpointInfo> _breakpoints = [];
    private readonly SemaphoreSlim _lifecycleOperationLock = new(1, 1);
    private readonly SemaphoreSlim _breakpointOperationLock = new(1, 1);
    private readonly Lock _gate = new();
    private TaskCompletionSource<bool> _stateChanged = CreateSignal();
    private Task _locationRefreshTask = Task.CompletedTask;
    private int _stateVersion;
    private int _stopGeneration;
    private int? _exitCode;
    private string? _currentSourcePath;
    private int? _currentSourceLine;
    private string? _currentFrameName;
    private SourceContext? _currentSourceContext;
    private readonly List<string> _recentOutput = [];
    private readonly HashSet<int> _knownThreadIds = [];
    private string[] _exceptionFilters = [];
    private int _resumeCommandActive;
    private int _launchCommandActive;
    private bool _isAttached;

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
        SourceContext? currentSourceContext;
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
            currentSourceContext = _currentSourceContext;
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
                frame = currentFrameName,
                context = currentSourceContext
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
        await _lifecycleOperationLock.WaitAsync(cancellationToken);
        try
        {
            await EnsureStartedCoreAsync(cancellationToken);
        }
        finally
        {
            _lifecycleOperationLock.Release();
        }
    }

    private async Task EnsureStartedCoreAsync(CancellationToken cancellationToken)
    {
        Backend = _backendResolver();
        if (!Backend.Available || Backend.Path is null)
        {
            throw new BackendUnavailableException(Backend.Error);
        }

        if (_dapClient is not null && _dapClient.IsRunning)
        {
            return;
        }

        var client = _dapClientFactory.Create(Backend.Path);
        _dapClient = client;
        client.EventReceived += OnDapEvent;
        client.Closed += OnDapClosed;
        SetState("initializing");
        try
        {
            await client.StartAsync(cancellationToken);
        }
        catch
        {
            client.EventReceived -= OnDapEvent;
            client.Closed -= OnDapClosed;
            try
            {
                await client.DisposeAsync();
            }
            catch
            {
                // Preserve the startup failure; the failed client is no longer reusable.
            }

            if (ReferenceEquals(_dapClient, client))
            {
                _dapClient = null;
            }

            SetState("idle");
            NotifyStateChanged();
            throw;
        }
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
            await _lifecycleOperationLock.WaitAsync(cancellationToken);
            try
            {
                if (State != "idle")
                {
                    throw new InvalidOperationException("start_debug requires an idle debugger session.");
                }

                try
                {
                    await EnsureStartedCoreAsync(cancellationToken);
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

                    await ConfigureAdapterAsync(cancellationToken);

                    var executionVersion = GetStateVersion();
                    await SendCheckedRequestAsync("configurationDone", cancellationToken: cancellationToken);
                    await launchTask;
                    _isAttached = false;
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
                catch
                {
                    await StopCoreAsync();
                    throw;
                }
            }
            finally
            {
                _lifecycleOperationLock.Release();
            }
        }
        finally
        {
            Volatile.Write(ref _launchCommandActive, 0);
        }
    }

    public async Task<object> AttachAsync(
        int processId,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), "Process id must be positive.");
        }

        if (Interlocked.CompareExchange(ref _launchCommandActive, 1, 0) != 0)
        {
            throw new InvalidOperationException("A launch or attach command is already in progress.");
        }

        try
        {
            await _lifecycleOperationLock.WaitAsync(cancellationToken);
            try
            {
                if (State != "idle")
                {
                    throw new InvalidOperationException("attach_debug requires an idle debugger session.");
                }

                try
                {
                    _isAttached = true;
                    await EnsureStartedCoreAsync(cancellationToken);
                    var attachTask = SendCheckedRequestAsync("attach", new JsonObject
                    {
                        ["processId"] = processId,
                        ["justMyCode"] = false
                    }, cancellationToken);

                    await WaitForStateAsync("initialized", DefaultExecutionTimeout, cancellationToken);
                    await ConfigureAdapterAsync(cancellationToken);
                    await SendCheckedRequestAsync("configurationDone", cancellationToken: cancellationToken);
                    await attachTask;
                    if (State is not "stopped" and not "terminated")
                    {
                        SetState("running");
                    }

                    return GetStatus();
                }
                catch
                {
                    await StopCoreAsync();
                    throw;
                }
            }
            finally
            {
                _lifecycleOperationLock.Release();
            }
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

    public async Task<object> SetExceptionBreakpointsAsync(
        IReadOnlyList<string> filters,
        CancellationToken cancellationToken = default)
    {
        var normalized = filters
            .Where(filter => !string.IsNullOrWhiteSpace(filter))
            .Select(filter => filter.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        lock (_gate)
        {
            _exceptionFilters = normalized;
        }

        if (_dapClient is not null && _dapClient.IsRunning && State is not "idle" and not "terminated")
        {
            await SyncExceptionBreakpointsAsync(cancellationToken);
        }

        return new { state = State, filters = normalized };
    }

    public async Task<object> GetExceptionInfoAsync(
        int? threadId = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedThreadId = threadId ?? RequireStoppedThreadId("get_exception_info");
        RequireStoppedState("get_exception_info");
        var response = await SendCheckedRequestAsync("exceptionInfo", new JsonObject
        {
            ["threadId"] = resolvedThreadId
        }, cancellationToken);

        return new
        {
            state = State,
            threadId = resolvedThreadId,
            exception = response["body"]?.DeepClone()
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
        await _lifecycleOperationLock.WaitAsync();
        try
        {
            return await StopCoreAsync();
        }
        finally
        {
            _lifecycleOperationLock.Release();
        }
    }

    private async Task<object> StopCoreAsync()
    {
        if (_dapClient is not null)
        {
            if (_dapClient.IsRunning && State is not "idle")
            {
                using var disconnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await SendCheckedRequestAsync("disconnect", new JsonObject
                    {
                        ["restart"] = false,
                        ["terminateDebuggee"] = !_isAttached
                    }, disconnectCts.Token);
                }
                catch
                {
                    // Disposal remains the fallback when graceful disconnect fails.
                }
            }

            await _dapClient.DisposeAsync();
            _dapClient = null;
        }

        SetState("idle");
        StopReason = null;
        CurrentThreadId = null;
        _isAttached = false;
        Interlocked.Increment(ref _stopGeneration);
        lock (_gate)
        {
            _exitCode = null;
            _currentSourcePath = null;
            _currentSourceLine = null;
            _currentFrameName = null;
            _currentSourceContext = null;
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
                var stopGeneration = Interlocked.Increment(ref _stopGeneration);
                if (CurrentThreadId is { } stoppedThreadId)
                {
                    lock (_gate)
                    {
                        _knownThreadIds.Add(stoppedThreadId);
                    }

                    var locationRefreshTask = RefreshCurrentLocationBestEffortAsync(
                        stoppedThreadId,
                        stopGeneration);
                    lock (_gate)
                    {
                        _locationRefreshTask = locationRefreshTask;
                    }
                }
                NotifyStateChanged();
                break;
            case "continued":
                Interlocked.Increment(ref _stopGeneration);
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
                .Where(item => SourcePathComparer.Equals(item.File, file))
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

    private async Task ConfigureAdapterAsync(CancellationToken cancellationToken)
    {
        await _breakpointOperationLock.WaitAsync(cancellationToken);
        try
        {
            string[] breakpointFiles;
            lock (_gate)
            {
                breakpointFiles = _breakpoints
                    .Select(item => item.File)
                    .Distinct(SourcePathComparer)
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

        await SyncExceptionBreakpointsAsync(cancellationToken);
    }

    private async Task SyncExceptionBreakpointsAsync(CancellationToken cancellationToken)
    {
        string[] filters;
        lock (_gate)
        {
            filters = _exceptionFilters.ToArray();
        }

        await SendCheckedRequestAsync("setExceptionBreakpoints", new JsonObject
        {
            ["filters"] = ToJsonArray(filters)
        }, cancellationToken);
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
                    await AwaitLocationRefreshBestEffortAsync(cancellationToken);
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

    private async Task AwaitLocationRefreshBestEffortAsync(CancellationToken cancellationToken)
    {
        Task refreshTask;
        lock (_gate)
        {
            refreshTask = _locationRefreshTask;
        }

        try
        {
            await refreshTask.WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
        catch (TimeoutException)
        {
            // Source location enriches a stop response but is not required for debugger control.
        }
    }

    private async Task RefreshCurrentLocationBestEffortAsync(int threadId, int stopGeneration)
    {
        try
        {
            if (_dapClient is null)
            {
                return;
            }

            var response = await SendCheckedRequestAsync("stackTrace", new JsonObject
            {
                ["threadId"] = threadId,
                ["startFrame"] = 0,
                ["levels"] = 1
            });

            var frame = response["body"]?["stackFrames"]?.AsArray()?.FirstOrDefault()?.AsObject();
            if (frame is null)
            {
                return;
            }

            var sourcePath = frame["source"]?["path"]?.GetValue<string>();
            var sourceLine = frame["line"]?.GetValue<int>();
            var sourceContext = SourceContextReader.TryRead(sourcePath, sourceLine);
            lock (_gate)
            {
                if (stopGeneration != _stopGeneration
                    || State != "stopped"
                    || CurrentThreadId != threadId)
                {
                    return;
                }

                _currentFrameName = frame["name"]?.GetValue<string>();
                _currentSourcePath = sourcePath;
                _currentSourceLine = sourceLine;
                _currentSourceContext = sourceContext;
            }
        }
        catch (Exception)
        {
            // Source location enriches stopped state but adapter support is not guaranteed.
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
                SourcePathComparer.Equals(item.File, sourcePath));

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
                SourcePathComparer.Equals(item.File, _currentSourcePath));

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
            _currentSourceContext = null;
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
