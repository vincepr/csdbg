using System.Text.Json;
using System.Text.Json.Nodes;
using Csdbg.Core;

if (args is ["--install-netcoredbg"])
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    using var httpClient = new HttpClient();
    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("csdbg/0.1");
    try
    {
        var asset = NetcoredbgRelease.GetCurrentAsset();
        var installer = new BackendInstaller(
            httpClient,
            new SafeBackendArchiveExtractor(),
            new ProcessCommandProbe());
        var result = await installer.InstallAsync(
            asset,
            BackendInstallPaths.GetInstallRoot(),
            timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return 0;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new { installed = false, error = ex.Message },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return 1;
    }
}

if (args is ["--check"])
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var checker = new BackendHealthChecker(BackendLocator.FindNetcoredbg, new ProcessCommandProbe());
    var result = await checker.CheckAsync(timeout.Token);
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    return result.Healthy ? 0 : 1;
}

await using var session = new DebugSession();
var server = new McpServer(session, Console.In, Console.Out);
await server.RunAsync();
return 0;

internal sealed class McpServer
{
    private const string ProtocolVersion = "2025-06-18";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly DebugSession _session;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly SemaphoreSlim _outputLock = new(1, 1);

    public McpServer(DebugSession session, TextReader input, TextWriter output)
    {
        _session = session;
        _input = input;
        _output = output;
    }

    public async Task RunAsync()
    {
        var pendingRequests = new List<Task>();
        string? line;
        while ((line = await _input.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonObject? request;
            try
            {
                request = JsonNode.Parse(line)?.AsObject();
            }
            catch (Exception ex)
            {
                await WriteResponseAsync(null, Error(-32700, $"Parse error: {ex.Message}"));
                continue;
            }

            if (request is null)
            {
                await WriteResponseAsync(null, Error(-32600, "Invalid JSON-RPC request."));
                continue;
            }

            if (!request.TryGetPropertyValue("id", out var idNode))
            {
                await HandleNotificationAsync(request);
                continue;
            }

            var id = idNode?.DeepClone();
            pendingRequests.RemoveAll(task => task.IsCompletedSuccessfully);
            pendingRequests.Add(ProcessRequestAsync(id, request));
        }

        await Task.WhenAll(pendingRequests);
    }

    private async Task ProcessRequestAsync(JsonNode? id, JsonObject request)
    {
        var response = await HandleRequestAsync(request);
        await WriteResponseAsync(id, response);
    }

    private async Task WriteResponseAsync(JsonNode? id, JsonObject body)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id
        };

        if (body.ContainsKey("error"))
        {
            response["error"] = body["error"]?.DeepClone();
        }
        else
        {
            response["result"] = body;
        }

        await _outputLock.WaitAsync();
        try
        {
            await _output.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
            await _output.FlushAsync();
        }
        finally
        {
            _outputLock.Release();
        }
    }

    private Task HandleNotificationAsync(JsonObject request)
    {
        // MCP clients send notifications/initialized after initialize. No response is required.
        return Task.CompletedTask;
    }

    private async Task<JsonObject> HandleRequestAsync(JsonObject request)
    {
        try
        {
            var method = request["method"]?.GetValue<string>();
            var parameters = request["params"] switch
            {
                null => null,
                JsonObject objectParameters => objectParameters,
                _ => throw new ArgumentException("JSON-RPC params must be an object.")
            };

            return method switch
            {
                "initialize" => Initialize(),
                "ping" => new JsonObject(),
                "tools/list" => ToolsList(),
                "tools/call" => await ToolsCallSafelyAsync(parameters),
                _ => Error(-32601, $"Method not found: {method}")
            };
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            return Error(-32602, ex.Message);
        }
        catch (Exception ex)
        {
            return Error(-32000, ex.Message);
        }
    }

    private static JsonObject Initialize()
    {
        return new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject()
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "csdbg",
                ["version"] = "0.1.0"
            }
        };
    }

    private static JsonObject ToolsList()
    {
        return new JsonObject
        {
            ["tools"] = new JsonArray
            {
                Tool(
                    "get_status",
                    "Return debugger session state and netcoredbg backend availability.",
                    new JsonObject()),
                Tool(
                    "start_debug",
                    "Launch a .NET program under netcoredbg.",
                    new JsonObject
                    {
                        ["program"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Path to the .NET DLL or executable."
                        },
                        ["cwd"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Optional working directory."
                        },
                        ["args"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" }
                        },
                        ["stopAtEntry"] = new JsonObject
                        {
                            ["type"] = "boolean"
                        }
                    },
                    ["program"]),
                Tool(
                    "attach_debug",
                    "Attach netcoredbg to an existing .NET process.",
                    new JsonObject
                    {
                        ["processId"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["minimum"] = 1,
                            ["description"] = "Operating-system process id to attach to."
                        }
                    },
                    ["processId"]),
                Tool(
                    "add_breakpoint",
                    "Add a source line breakpoint and sync it to netcoredbg when active.",
                    new JsonObject
                    {
                        ["file"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Source file path."
                        },
                        ["line"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "1-based line number."
                        },
                        ["condition"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Optional breakpoint condition."
                        }
                    },
                    ["file", "line"]),
                Tool(
                    "remove_breakpoint",
                    "Remove a source line breakpoint by csdbg breakpoint id.",
                    new JsonObject
                    {
                        ["id"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Breakpoint id returned by add_breakpoint."
                        }
                    },
                    ["id"]),
                Tool(
                    "continue_execution",
                    "Continue the stopped debuggee and wait until it stops again, exits, or times out.",
                    new JsonObject
                    {
                        ["timeoutMs"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Optional wait timeout in milliseconds."
                        }
                    }),
                Tool(
                    "pause_execution",
                    "Pause a running debuggee thread and wait until it stops.",
                    new JsonObject
                    {
                        ["threadId"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Optional thread id. Defaults to the current or first known thread."
                        },
                        ["timeoutMs"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Optional wait timeout in milliseconds."
                        }
                    }),
                Tool(
                    "step_over",
                    "Step over the current line and wait until the debuggee stops again, exits, or times out.",
                    new JsonObject
                    {
                        ["timeoutMs"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Optional wait timeout in milliseconds."
                        }
                    }),
                Tool(
                    "step_into",
                    "Step into from the current line and wait until the debuggee stops again, exits, or times out.",
                    new JsonObject
                    {
                        ["timeoutMs"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Optional wait timeout in milliseconds."
                        }
                    }),
                Tool(
                    "step_out",
                    "Step out of the current frame and wait until the debuggee stops again, exits, or times out.",
                    new JsonObject
                    {
                        ["timeoutMs"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Optional wait timeout in milliseconds."
                        }
                    }),
                Tool(
                    "get_threads",
                    "Return debugger threads for the active session.",
                    new JsonObject()),
                Tool(
                    "get_call_stack",
                    "Return stack frames for a stopped thread.",
                    new JsonObject
                    {
                        ["threadId"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Optional thread id. Defaults to the current stopped thread."
                        },
                        ["startFrame"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Optional first frame index."
                        },
                        ["levels"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Optional number of frames to return."
                        }
                    }),
                Tool(
                    "get_scopes",
                    "Return scopes for a stack frame.",
                    new JsonObject
                    {
                        ["frameId"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "DAP frame id returned by get_call_stack."
                        }
                    },
                    ["frameId"]),
                Tool(
                    "get_variables",
                    "Return variables for a scope or expandable variable reference.",
                    new JsonObject
                    {
                        ["variablesReference"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "DAP variablesReference returned by get_scopes or get_variables."
                        },
                        ["start"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Optional first variable index."
                        },
                        ["count"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Optional number of variables to return."
                        }
                    },
                    ["variablesReference"]),
                Tool(
                    "evaluate_expression",
                    "Evaluate an expression in a stopped stack frame.",
                    new JsonObject
                    {
                        ["expression"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Expression to evaluate."
                        },
                        ["frameId"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["description"] = "Optional DAP frame id returned by get_call_stack."
                        },
                        ["context"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "Optional DAP evaluation context. Defaults to watch."
                        },
                        ["unsafe"] = new JsonObject
                        {
                            ["type"] = "boolean",
                            ["description"] = "Allow expressions that may execute user code or mutate state."
                        }
                    },
                    ["expression"]),
                Tool(
                    "set_exception_breakpoints",
                    "Configure exception stop filters advertised by netcoredbg.",
                    new JsonObject
                    {
                        ["filters"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" }
                        }
                    },
                    ["filters"]),
                Tool(
                    "get_exception_info",
                    "Return details for the current exception stop.",
                    new JsonObject
                    {
                        ["threadId"] = new JsonObject { ["type"] = "integer" }
                    }),
                Tool(
                    "stop_debug",
                    "Stop the active debugger adapter and debuggee process.",
                    new JsonObject())
            }
        };
    }

    private async Task<JsonObject> ToolsCallAsync(JsonObject? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>();

        return name switch
        {
            "get_status" => ToolResult(_session.GetStatus()),
            "start_debug" => ToolResult(await StartDebugAsync(ToolArguments(parameters))),
            "attach_debug" => ToolResult(await AttachDebugAsync(ToolArguments(parameters))),
            "add_breakpoint" => ToolResult(await AddBreakpointAsync(ToolArguments(parameters))),
            "remove_breakpoint" => ToolResult(await RemoveBreakpointAsync(ToolArguments(parameters))),
            "continue_execution" => ToolResult(await ContinueExecutionAsync(ToolArguments(parameters))),
            "pause_execution" => ToolResult(await PauseExecutionAsync(ToolArguments(parameters))),
            "step_over" => ToolResult(await StepOverAsync(ToolArguments(parameters))),
            "step_into" => ToolResult(await StepIntoAsync(ToolArguments(parameters))),
            "step_out" => ToolResult(await StepOutAsync(ToolArguments(parameters))),
            "get_threads" => ToolResult(await _session.GetThreadsAsync()),
            "get_call_stack" => ToolResult(await GetCallStackAsync(ToolArguments(parameters))),
            "get_scopes" => ToolResult(await GetScopesAsync(ToolArguments(parameters))),
            "get_variables" => ToolResult(await GetVariablesAsync(ToolArguments(parameters))),
            "evaluate_expression" => ToolResult(await EvaluateExpressionAsync(ToolArguments(parameters))),
            "set_exception_breakpoints" => ToolResult(await SetExceptionBreakpointsAsync(ToolArguments(parameters))),
            "get_exception_info" => ToolResult(await GetExceptionInfoAsync(ToolArguments(parameters))),
            "stop_debug" => ToolResult(await _session.StopAsync()),
            _ => Error(-32602, $"Unknown tool: {name}")
        };
    }

    private async Task<JsonObject> ToolsCallSafelyAsync(JsonObject? parameters)
    {
        try
        {
            return await ToolsCallAsync(parameters);
        }
        catch (Exception ex)
        {
            return ToolError(ex);
        }
    }

    private async Task<object> StartDebugAsync(JsonObject? arguments)
    {
        var program = RequiredString(arguments, "program");
        var cwd = OptionalString(arguments, "cwd");
        var args = OptionalStringArray(arguments, "args");
        var stopAtEntry = arguments?["stopAtEntry"]?.GetValue<bool>() ?? false;

        return await _session.LaunchAsync(program, cwd, args, stopAtEntry);
    }

    private async Task<object> AttachDebugAsync(JsonObject? arguments)
    {
        var processId = arguments?["processId"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Missing required argument: processId");
        return await _session.AttachAsync(processId);
    }

    private async Task<object> AddBreakpointAsync(JsonObject? arguments)
    {
        var file = RequiredString(arguments, "file");
        var line = arguments?["line"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Missing required argument: line");
        var condition = OptionalString(arguments, "condition");

        return await _session.AddBreakpointAsync(file, line, condition);
    }

    private async Task<object> RemoveBreakpointAsync(JsonObject? arguments)
    {
        var id = RequiredString(arguments, "id");
        return await _session.RemoveBreakpointAsync(id);
    }

    private async Task<object> ContinueExecutionAsync(JsonObject? arguments)
    {
        return await _session.ContinueAsync(ReadTimeout(arguments));
    }

    private async Task<object> PauseExecutionAsync(JsonObject? arguments)
    {
        return await _session.PauseAsync(
            OptionalInt(arguments, "threadId"),
            ReadTimeout(arguments));
    }

    private async Task<object> StepOverAsync(JsonObject? arguments)
    {
        return await _session.StepOverAsync(ReadTimeout(arguments));
    }

    private async Task<object> StepIntoAsync(JsonObject? arguments)
    {
        return await _session.StepIntoAsync(ReadTimeout(arguments));
    }

    private async Task<object> StepOutAsync(JsonObject? arguments)
    {
        return await _session.StepOutAsync(ReadTimeout(arguments));
    }

    private async Task<object> GetCallStackAsync(JsonObject? arguments)
    {
        return await _session.GetCallStackAsync(
            OptionalInt(arguments, "threadId"),
            OptionalInt(arguments, "startFrame") ?? 0,
            OptionalInt(arguments, "levels") ?? 20);
    }

    private async Task<object> GetScopesAsync(JsonObject? arguments)
    {
        var frameId = arguments?["frameId"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Missing required argument: frameId");

        return await _session.GetScopesAsync(frameId);
    }

    private async Task<object> GetVariablesAsync(JsonObject? arguments)
    {
        var variablesReference = arguments?["variablesReference"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Missing required argument: variablesReference");

        return await _session.GetVariablesAsync(
            variablesReference,
            OptionalInt(arguments, "start"),
            OptionalInt(arguments, "count"));
    }

    private async Task<object> EvaluateExpressionAsync(JsonObject? arguments)
    {
        var expression = RequiredString(arguments, "expression");
        return await _session.EvaluateExpressionAsync(
            expression,
            OptionalInt(arguments, "frameId"),
            OptionalString(arguments, "context"),
            arguments?["unsafe"]?.GetValue<bool>() ?? false);
    }

    private async Task<object> SetExceptionBreakpointsAsync(JsonObject? arguments)
    {
        var filters = OptionalStringArray(arguments, "filters");
        if (arguments?["filters"] is null)
        {
            throw new InvalidOperationException("Missing required argument: filters");
        }

        return await _session.SetExceptionBreakpointsAsync(filters);
    }

    private async Task<object> GetExceptionInfoAsync(JsonObject? arguments)
    {
        return await _session.GetExceptionInfoAsync(OptionalInt(arguments, "threadId"));
    }

    private static JsonObject Tool(
        string name,
        string description,
        JsonObject properties,
        IEnumerable<string>? required = null)
    {
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = ToJsonArray(required ?? []),
                ["additionalProperties"] = false
            }
        };
    }

    private JsonObject ToolResult(object value)
    {
        var envelope = new
        {
            state = _session.State,
            data = value,
            nextActions = NextActionsForState(_session.State)
        };
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        return new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = json
                }
            }
        };
    }

    private JsonObject ToolError(Exception exception)
    {
        var envelope = new
        {
            state = _session.State,
            error = new
            {
                code = ClassifyToolError(exception),
                message = exception.Message
            },
            nextActions = NextActionsForState(_session.State)
        };

        return new JsonObject
        {
            ["isError"] = true,
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = JsonSerializer.Serialize(envelope, JsonOptions)
                }
            }
        };
    }

    private static string ClassifyToolError(Exception exception)
    {
        if (exception is TimeoutException)
        {
            return "timeout";
        }

        if (exception is ArgumentException or FormatException or JsonException ||
            exception.Message.StartsWith("Missing required argument:", StringComparison.Ordinal))
        {
            return "invalid_arguments";
        }

        if (exception is BackendUnavailableException)
        {
            return "backend_unavailable";
        }

        if (exception is InvalidOperationException &&
            (exception.Message.Contains("requires", StringComparison.OrdinalIgnoreCase) ||
             exception.Message.Contains("not active", StringComparison.OrdinalIgnoreCase) ||
             exception.Message.Contains("already in progress", StringComparison.OrdinalIgnoreCase)))
        {
            return "wrong_state";
        }

        return "debugger_error";
    }

    private static string[] NextActionsForState(string state)
    {
        return state switch
        {
            "idle" => ["start_debug", "attach_debug", "add_breakpoint", "get_status"],
            "running" => ["pause_execution", "get_status", "stop_debug"],
            "stopped" =>
            [
                "get_call_stack",
                "get_scopes",
                "get_variables",
                "evaluate_expression",
                "get_exception_info",
                "step_over",
                "step_into",
                "step_out",
                "continue_execution",
                "stop_debug"
            ],
            "terminated" => ["get_status", "stop_debug"],
            _ => ["get_status", "stop_debug"]
        };
    }

    private static JsonObject Error(int code, string message)
    {
        return new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };
    }

    private static string RequiredString(JsonObject? arguments, string name)
    {
        return OptionalString(arguments, name)
            ?? throw new InvalidOperationException($"Missing required argument: {name}");
    }

    private static JsonObject? ToolArguments(JsonObject? parameters)
    {
        var arguments = parameters?["arguments"];
        return arguments switch
        {
            null => null,
            JsonObject objectArguments => objectArguments,
            _ => throw new ArgumentException("Tool arguments must be a JSON object.", nameof(parameters))
        };
    }

    private static string? OptionalString(JsonObject? arguments, string name)
    {
        return arguments?[name]?.GetValue<string>();
    }

    private static string[] OptionalStringArray(JsonObject? arguments, string name)
    {
        var node = arguments?[name];
        if (node is null)
        {
            return [];
        }

        if (node is not JsonArray array)
        {
            throw new ArgumentException($"Argument '{name}' must be an array of strings.");
        }

        var values = new List<string>();
        foreach (var item in array)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var text))
            {
                throw new ArgumentException($"Argument '{name}' must contain only strings.");
            }

            values.Add(text);
        }

        return values.ToArray();
    }

    private static TimeSpan? ReadTimeout(JsonObject? arguments)
    {
        var timeoutMs = arguments?["timeoutMs"]?.GetValue<int>();
        return timeoutMs is > 0 ? TimeSpan.FromMilliseconds(timeoutMs.Value) : null;
    }

    private static int? OptionalInt(JsonObject? arguments, string name)
    {
        return arguments?[name]?.GetValue<int>();
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }
}
