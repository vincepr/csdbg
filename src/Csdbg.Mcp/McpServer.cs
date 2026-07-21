using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Csdbg.Core;

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
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requestCancellations = new();

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

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(line);
            }
            catch (JsonException ex)
            {
                await WriteResponseAsync(null, Error(-32700, $"Parse error: {ex.Message}"));
                continue;
            }

            if (parsed is not JsonObject request || !IsValidRequestEnvelope(request))
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
            var requestKey = RequestKey(idNode);
            var cancellation = new CancellationTokenSource();
            if (!_requestCancellations.TryAdd(requestKey, cancellation))
            {
                cancellation.Dispose();
                await WriteResponseAsync(id, Error(-32600, "A request with this id is already active."));
                continue;
            }

            pendingRequests.RemoveAll(task => task.IsCompleted);
            pendingRequests.Add(ProcessRequestAsync(id, request, requestKey, cancellation));
        }

        foreach (var cancellation in _requestCancellations.Values)
        {
            cancellation.Cancel();
        }

        await Task.WhenAll(pendingRequests);
    }

    private async Task ProcessRequestAsync(
        JsonNode? id,
        JsonObject request,
        string requestKey,
        CancellationTokenSource cancellation)
    {
        try
        {
            var response = await HandleRequestAsync(request, cancellation.Token);
            await WriteResponseAsync(id, response);
        }
        catch (OperationCanceledException)
        {
            await WriteResponseAsync(id, Error(-32800, "Request cancelled."));
        }
        finally
        {
            _requestCancellations.TryRemove(requestKey, out _);
            cancellation.Dispose();
        }
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
        if (request["method"]?.GetValue<string>() == "notifications/cancelled"
            && request["params"] is JsonObject parameters
            && parameters.TryGetPropertyValue("requestId", out var requestId))
        {
            var requestKey = RequestKey(requestId);
            if (_requestCancellations.TryGetValue(requestKey, out var cancellation))
            {
                cancellation.Cancel();
            }
        }

        return Task.CompletedTask;
    }

    private static bool IsValidRequestEnvelope(JsonObject request)
    {
        if (request["jsonrpc"] is not JsonValue jsonrpc
            || !jsonrpc.TryGetValue<string>(out var version)
            || version != "2.0"
            || request["method"] is not JsonValue method
            || !method.TryGetValue<string>(out var methodName)
            || string.IsNullOrWhiteSpace(methodName))
        {
            return false;
        }

        return !request.TryGetPropertyValue("id", out var id)
            || id is null
            || id.GetValueKind() is JsonValueKind.String or JsonValueKind.Number;
    }

    private static string RequestKey(JsonNode? id) => id?.ToJsonString() ?? "null";

    private async Task<JsonObject> HandleRequestAsync(
        JsonObject request,
        CancellationToken cancellationToken)
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
                "tools/call" => await HandleToolsCallAsync(parameters, cancellationToken),
                _ => Error(-32601, $"Method not found: {method}")
            };
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            return Error(-32602, ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
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
                            ["minimum"] = 1,
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

    private async Task<JsonObject> HandleToolsCallAsync(
        JsonObject? parameters,
        CancellationToken cancellationToken)
    {
        var (name, arguments) = ValidateToolCall(parameters);
        return await ToolsCallSafelyAsync(name, arguments, cancellationToken);
    }

    private static (string Name, JsonObject Arguments) ValidateToolCall(JsonObject? parameters)
    {
        if (parameters is null)
        {
            throw new ArgumentException("tools/call params must be an object.");
        }

        var unexpectedParameter = parameters
            .Select(property => property.Key)
            .FirstOrDefault(name => name is not "name" and not "arguments");
        if (unexpectedParameter is not null)
        {
            throw new ArgumentException($"Unknown tools/call parameter: {unexpectedParameter}");
        }

        if (parameters["name"] is not JsonValue nameValue
            || !nameValue.TryGetValue<string>(out var name)
            || string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("tools/call requires a string name.");
        }

        var arguments = parameters["arguments"] switch
        {
            null => new JsonObject(),
            JsonObject objectArguments => objectArguments,
            _ => throw new ArgumentException("tools/call arguments must be an object.")
        };

        var tool = ToolsList()["tools"]!.AsArray()
            .Select(item => item!.AsObject())
            .FirstOrDefault(item => item["name"]?.GetValue<string>() == name)
            ?? throw new ArgumentException($"Unknown tool: {name}");
        var schema = tool["inputSchema"]!.AsObject();
        var properties = schema["properties"]!.AsObject();

        foreach (var argument in arguments)
        {
            if (!properties.ContainsKey(argument.Key))
            {
                throw new ArgumentException($"Unknown argument for {name}: {argument.Key}");
            }

            ValidateSchemaValue(name, argument.Key, argument.Value, properties[argument.Key]!.AsObject());
        }

        foreach (var required in schema["required"]!.AsArray())
        {
            var requiredName = required!.GetValue<string>();
            if (!arguments.ContainsKey(requiredName) || arguments[requiredName] is null)
            {
                throw new ArgumentException($"Missing required argument: {requiredName}");
            }
        }

        return (name, arguments);
    }

    private static void ValidateSchemaValue(
        string toolName,
        string argumentName,
        JsonNode? value,
        JsonObject schema)
    {
        var type = schema["type"]?.GetValue<string>();
        var valid = type switch
        {
            "string" => value is JsonValue stringValue && stringValue.TryGetValue<string>(out _),
            "integer" => value is JsonValue integerValue && integerValue.TryGetValue<int>(out _),
            "boolean" => value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
            "array" => value is JsonArray,
            _ => false
        };
        if (!valid)
        {
            throw new ArgumentException($"Argument '{argumentName}' for {toolName} must be {type}.");
        }

        if (type == "integer"
            && schema["minimum"]?.GetValue<int?>() is { } minimum
            && value!.GetValue<int>() < minimum)
        {
            throw new ArgumentException(
                $"Argument '{argumentName}' for {toolName} must be at least {minimum}.");
        }

        if (type == "array"
            && schema["items"]?["type"]?.GetValue<string>() == "string"
            && value!.AsArray().Any(item => item is not JsonValue itemValue || !itemValue.TryGetValue<string>(out _)))
        {
            throw new ArgumentException($"Argument '{argumentName}' for {toolName} must contain only strings.");
        }
    }

    private async Task<JsonObject> ToolsCallAsync(
        string name,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {

        return name switch
        {
            "get_status" => ToolResult(_session.GetStatus()),
            "start_debug" => ToolResult(await StartDebugAsync(arguments, cancellationToken)),
            "attach_debug" => ToolResult(await AttachDebugAsync(arguments, cancellationToken)),
            "add_breakpoint" => ToolResult(await AddBreakpointAsync(arguments, cancellationToken)),
            "remove_breakpoint" => ToolResult(await RemoveBreakpointAsync(arguments, cancellationToken)),
            "continue_execution" => ToolResult(await ContinueExecutionAsync(arguments, cancellationToken)),
            "pause_execution" => ToolResult(await PauseExecutionAsync(arguments, cancellationToken)),
            "step_over" => ToolResult(await StepOverAsync(arguments, cancellationToken)),
            "step_into" => ToolResult(await StepIntoAsync(arguments, cancellationToken)),
            "step_out" => ToolResult(await StepOutAsync(arguments, cancellationToken)),
            "get_threads" => ToolResult(await _session.GetThreadsAsync(cancellationToken)),
            "get_call_stack" => ToolResult(
                await GetCallStackAsync(arguments, cancellationToken),
                "get_call_stack"),
            "get_scopes" => ToolResult(await GetScopesAsync(arguments, cancellationToken), "get_scopes"),
            "get_variables" => ToolResult(
                await GetVariablesAsync(arguments, cancellationToken),
                "get_variables"),
            "evaluate_expression" => ToolResult(await EvaluateExpressionAsync(arguments, cancellationToken)),
            "set_exception_breakpoints" => ToolResult(await SetExceptionBreakpointsAsync(arguments, cancellationToken)),
            "get_exception_info" => ToolResult(await GetExceptionInfoAsync(arguments, cancellationToken)),
            "stop_debug" => ToolResult(await _session.StopAsync()),
            _ => Error(-32602, $"Unknown tool: {name}")
        };
    }

    private async Task<JsonObject> ToolsCallSafelyAsync(
        string name,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ToolsCallAsync(name, arguments, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolError(ex);
        }
    }

    private async Task<object> StartDebugAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var program = RequiredString(arguments, "program");
        var cwd = OptionalString(arguments, "cwd");
        var args = OptionalStringArray(arguments, "args");
        var stopAtEntry = arguments?["stopAtEntry"]?.GetValue<bool>() ?? false;

        return await _session.LaunchAsync(program, cwd, args, stopAtEntry, cancellationToken);
    }

    private async Task<object> AttachDebugAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var processId = arguments?["processId"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Missing required argument: processId");
        return await _session.AttachAsync(processId, cancellationToken);
    }

    private async Task<object> AddBreakpointAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var file = RequiredString(arguments, "file");
        var line = arguments?["line"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Missing required argument: line");
        var condition = OptionalString(arguments, "condition");

        return await _session.AddBreakpointAsync(file, line, condition, cancellationToken);
    }

    private async Task<object> RemoveBreakpointAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var id = RequiredString(arguments, "id");
        return await _session.RemoveBreakpointAsync(id, cancellationToken);
    }

    private async Task<object> ContinueExecutionAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        return await _session.ContinueAsync(ReadTimeout(arguments), cancellationToken);
    }

    private async Task<object> PauseExecutionAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        return await _session.PauseAsync(
            OptionalInt(arguments, "threadId"),
            ReadTimeout(arguments),
            cancellationToken);
    }

    private async Task<object> StepOverAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        return await _session.StepOverAsync(ReadTimeout(arguments), cancellationToken);
    }

    private async Task<object> StepIntoAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        return await _session.StepIntoAsync(ReadTimeout(arguments), cancellationToken);
    }

    private async Task<object> StepOutAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        return await _session.StepOutAsync(ReadTimeout(arguments), cancellationToken);
    }

    private async Task<object> GetCallStackAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        return await _session.GetCallStackAsync(
            OptionalInt(arguments, "threadId"),
            OptionalInt(arguments, "startFrame") ?? 0,
            OptionalInt(arguments, "levels") ?? 20,
            cancellationToken);
    }

    private async Task<object> GetScopesAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var frameId = arguments?["frameId"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Missing required argument: frameId");

        return await _session.GetScopesAsync(frameId, cancellationToken);
    }

    private async Task<object> GetVariablesAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var variablesReference = arguments?["variablesReference"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Missing required argument: variablesReference");

        return await _session.GetVariablesAsync(
            variablesReference,
            OptionalInt(arguments, "start"),
            OptionalInt(arguments, "count"),
            cancellationToken);
    }

    private async Task<object> EvaluateExpressionAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var expression = RequiredString(arguments, "expression");
        return await _session.EvaluateExpressionAsync(
            expression,
            OptionalInt(arguments, "frameId"),
            OptionalString(arguments, "context"),
            arguments["unsafe"]?.GetValue<bool>() ?? false,
            cancellationToken);
    }

    private async Task<object> SetExceptionBreakpointsAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var filters = OptionalStringArray(arguments, "filters");
        if (arguments?["filters"] is null)
        {
            throw new InvalidOperationException("Missing required argument: filters");
        }

        return await _session.SetExceptionBreakpointsAsync(filters, cancellationToken);
    }

    private async Task<object> GetExceptionInfoAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        return await _session.GetExceptionInfoAsync(OptionalInt(arguments, "threadId"), cancellationToken);
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

    private JsonObject ToolResult(object value, string? completedTool = null)
    {
        var envelope = new
        {
            state = _session.State,
            data = value,
            nextActions = NextActionsForState(_session.State, _session.StopReason, completedTool)
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
            nextActions = NextActionsForState(_session.State, _session.StopReason)
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

    private static string[] NextActionsForState(
        string state,
        string? stopReason,
        string? completedTool = null)
    {
        return state switch
        {
            "idle" => ["start_debug", "attach_debug", "add_breakpoint", "get_status"],
            "running" => ["pause_execution", "get_status", "stop_debug"],
            "stopped" => StoppedNextActions(stopReason, completedTool),
            "terminated" => ["get_status", "stop_debug"],
            _ => ["get_status", "stop_debug"]
        };
    }

    private static string[] StoppedNextActions(string? stopReason, string? completedTool)
    {
        var inspection = completedTool switch
        {
            "get_call_stack" => new[] { "get_scopes", "evaluate_expression" },
            "get_scopes" => ["get_variables", "evaluate_expression"],
            "get_variables" => ["get_variables", "evaluate_expression"],
            _ when stopReason == "exception" => ["get_exception_info", "get_call_stack"],
            _ => new[] { "get_call_stack" }
        };

        return
        [
            .. inspection,
            "step_over",
            "step_into",
            "step_out",
            "continue_execution",
            "stop_debug"
        ];
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
