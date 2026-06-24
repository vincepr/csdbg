using System.Text.Json;
using System.Text.Json.Nodes;
using Csdbg.Core;

await using var session = new DebugSession();
var server = new McpServer(session, Console.In, Console.Out);
await server.RunAsync();

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

    public McpServer(DebugSession session, TextReader input, TextWriter output)
    {
        _session = session;
        _input = input;
        _output = output;
    }

    public async Task RunAsync()
    {
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
            var response = await HandleRequestAsync(request);
            await WriteResponseAsync(id, response);
        }
    }

    private Task HandleNotificationAsync(JsonObject request)
    {
        // MCP clients send notifications/initialized after initialize. No response is required.
        return Task.CompletedTask;
    }

    private async Task<JsonObject> HandleRequestAsync(JsonObject request)
    {
        var method = request["method"]?.GetValue<string>();
        var parameters = request["params"]?.AsObject();

        try
        {
            return method switch
            {
                "initialize" => Initialize(),
                "ping" => new JsonObject(),
                "tools/list" => ToolsList(),
                "tools/call" => await ToolsCallAsync(parameters),
                _ => Error(-32601, $"Method not found: {method}")
            };
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
            "start_debug" => ToolResult(await StartDebugAsync(parameters?["arguments"]?.AsObject())),
            "add_breakpoint" => ToolResult(await AddBreakpointAsync(parameters?["arguments"]?.AsObject())),
            "continue_execution" => ToolResult(await ContinueExecutionAsync(parameters?["arguments"]?.AsObject())),
            "step_over" => ToolResult(await StepOverAsync(parameters?["arguments"]?.AsObject())),
            "get_threads" => ToolResult(await _session.GetThreadsAsync()),
            "get_call_stack" => ToolResult(await GetCallStackAsync(parameters?["arguments"]?.AsObject())),
            "get_scopes" => ToolResult(await GetScopesAsync(parameters?["arguments"]?.AsObject())),
            "get_variables" => ToolResult(await GetVariablesAsync(parameters?["arguments"]?.AsObject())),
            "stop_debug" => ToolResult(await _session.StopAsync()),
            _ => Error(-32602, $"Unknown tool: {name}")
        };
    }

    private async Task<object> StartDebugAsync(JsonObject? arguments)
    {
        var program = RequiredString(arguments, "program");
        var cwd = OptionalString(arguments, "cwd");
        var args = OptionalStringArray(arguments, "args");
        var stopAtEntry = arguments?["stopAtEntry"]?.GetValue<bool>() ?? false;

        return await _session.LaunchAsync(program, cwd, args, stopAtEntry);
    }

    private async Task<object> AddBreakpointAsync(JsonObject? arguments)
    {
        var file = RequiredString(arguments, "file");
        var line = arguments?["line"]?.GetValue<int>()
            ?? throw new InvalidOperationException("Missing required argument: line");
        var condition = OptionalString(arguments, "condition");

        return await _session.AddBreakpointAsync(file, line, condition);
    }

    private async Task<object> ContinueExecutionAsync(JsonObject? arguments)
    {
        return await _session.ContinueAsync(ReadTimeout(arguments));
    }

    private async Task<object> StepOverAsync(JsonObject? arguments)
    {
        return await _session.StepOverAsync(ReadTimeout(arguments));
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

    private static JsonObject ToolResult(object value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
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

        await _output.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
        await _output.FlushAsync();
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
        var array = arguments?[name]?.AsArray();
        if (array is null)
        {
            return [];
        }

        return array
            .Select(item => item?.GetValue<string>())
            .Where(item => item is not null)
            .Cast<string>()
            .ToArray();
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
