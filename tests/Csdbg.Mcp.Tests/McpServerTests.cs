using System.Text.Json;
using System.Text.Json.Nodes;
using Csdbg.Core;
using Csdbg.Core.Dap;

namespace Csdbg.Mcp.Tests;

public sealed class McpServerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    public static TheoryData<string> InvalidRequestEnvelopes => new()
    {
        "null",
        "true",
        "42",
        "\"request\"",
        "[]",
        "{\"id\":1,\"method\":\"start_debug\"}",
        "{\"jsonrpc\":\"1.0\",\"id\":1,\"method\":\"start_debug\"}",
        "{\"jsonrpc\":2,\"id\":1,\"method\":\"start_debug\"}",
        "{\"jsonrpc\":\"2.0\",\"id\":1}",
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":7}",
        "{\"jsonrpc\":\"2.0\",\"id\":true,\"method\":\"start_debug\"}",
        "{\"jsonrpc\":\"2.0\",\"id\":{},\"method\":\"start_debug\"}"
    };

    public static TheoryData<string> InvalidToolCalls => new()
    {
        "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\"}",
        "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":[]}",
        "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":\"bad\"}",
        "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":7}}",
        "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":\"unknown\"}}",
        "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":\"start_debug\",\"extra\":true}}",
        "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":\"start_debug\",\"arguments\":[]}}",
        "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":\"start_debug\",\"arguments\":{}}}",
        "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":\"add_breakpoint\",\"arguments\":{\"file\":\"test.cs\",\"line\":1,\"extra\":true}}}",
        "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":\"add_breakpoint\",\"arguments\":{\"file\":\"test.cs\",\"line\":0}}}",
        "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":\"add_breakpoint\",\"arguments\":{\"file\":\"test.cs\",\"line\":-1}}}",
        "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":\"add_breakpoint\",\"arguments\":{\"file\":4,\"line\":1}}}",
        "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":\"add_breakpoint\",\"arguments\":{\"file\":\"test.cs\",\"line\":\"1\"}}}",
        "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":\"start_debug\",\"arguments\":{\"program\":\"test.dll\",\"stopAtEntry\":\"true\"}}}",
        "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":\"start_debug\",\"arguments\":{\"program\":\"test.dll\",\"args\":\"one\"}}}",
        "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":\"start_debug\",\"arguments\":{\"program\":\"test.dll\",\"args\":[1]}}}",
        "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":\"set_exception_breakpoints\",\"arguments\":{\"filters\":\"all\"}}}"
    };

    [Fact(Timeout = 10_000)]
    public async Task InitializeReturnsJsonRpcResult()
    {
        var response = await RunServerAsync(InitializeRequest(1));

        AssertJsonRpcResult(response, 1);
        var result = response["result"]!.AsObject();
        Assert.Equal("2025-06-18", result["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]);
        Assert.Equal("csdbg", result["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("0.2.0", result["serverInfo"]!["version"]!.GetValue<string>());
        var instructions = result["instructions"]!.GetValue<string>();
        Assert.Contains("get_status", instructions, StringComparison.Ordinal);
        Assert.Contains("stop_debug", instructions, StringComparison.Ordinal);
    }

    [Theory(Timeout = 10_000)]
    [InlineData("params")]
    [InlineData("protocolVersion")]
    [InlineData("capabilities")]
    [InlineData("clientInfo")]
    [InlineData("clientInfo.name")]
    [InlineData("clientInfo.version")]
    public async Task InitializeMissingRequiredFieldReturnsInvalidParams(string missingField)
    {
        var request = InitializeRequest(6);
        var parameters = request["params"]!.AsObject();
        switch (missingField)
        {
            case "params":
                request.Remove("params");
                break;
            case "clientInfo.name":
                parameters["clientInfo"]!.AsObject().Remove("name");
                break;
            case "clientInfo.version":
                parameters["clientInfo"]!.AsObject().Remove("version");
                break;
            default:
                parameters.Remove(missingField);
                break;
        }

        var response = await RunServerAsync(request);

        AssertJsonRpcError(response, 6, -32602);
    }

    [Fact(Timeout = 10_000)]
    public async Task GetStatusWithoutArgumentsReturnsNormalizedTextEnvelope()
    {
        var response = await RunServerAsync(CallTool(2, "get_status"));

        var envelope = AssertSuccessfulToolResult(response, 2);
        AssertEnvelopeRoot(envelope);
        Assert.Equal("idle", envelope["state"]!.GetValue<string>());
    }

    [Fact(Timeout = 10_000)]
    public async Task GetStatusWithNullArgumentsReturnsInvalidParamsWithoutMutatingSession()
    {
        var client = new ScriptedDapClient();
        await using var session = CreateSession(client);
        var before = JsonSerializer.SerializeToNode(session.GetStatus());

        var response = await RunServerAsync(
            session,
            CallTool(9, "get_status", null).ToJsonString());

        AssertJsonRpcError(response, 9, -32602);
        Assert.True(JsonNode.DeepEquals(before, JsonSerializer.SerializeToNode(session.GetStatus())));
        Assert.Equal(0, client.CreateCount);
    }

    [Fact(Timeout = 10_000)]
    public async Task AddBreakpointReturnsNormalizedTextEnvelope()
    {
        var arguments = new JsonObject
        {
            ["file"] = Path.Combine(Path.GetTempPath(), "Program.cs"),
            ["line"] = 12
        };

        var response = await RunServerAsync(CallTool(3, "add_breakpoint", arguments));

        var envelope = AssertSuccessfulToolResult(response, 3);
        AssertEnvelopeRoot(envelope);
        Assert.Equal("idle", envelope["state"]!.GetValue<string>());
    }

    [Fact(Timeout = 10_000)]
    public async Task ContinueInWrongStateReturnsToolError()
    {
        var response = await RunServerAsync(CallTool(4, "continue_execution", new JsonObject()));

        AssertToolError(response, 4, "wrong_state");
    }

    [Fact(Timeout = 10_000)]
    public async Task StartWithoutBackendReturnsToolError()
    {
        var arguments = new JsonObject
        {
            ["program"] = Path.Combine(Path.GetTempPath(), "app.dll")
        };

        var response = await RunServerAsync(
            CallTool(5, "start_debug", arguments),
            new BackendInfo { Error = "netcoredbg not found for this test." });

        AssertToolError(response, 5, "backend_unavailable");
    }

    [Fact(Timeout = 10_000)]
    public async Task EmptyRequiredStringReturnsToolError()
    {
        var response = await RunServerAsync(CallTool(7, "add_breakpoint", new JsonObject
        {
            ["file"] = string.Empty,
            ["line"] = 12
        }));

        AssertToolError(response, 7, "invalid_arguments");
    }

    [Fact(Timeout = 10_000)]
    public async Task UnknownMethodReturnsJsonRpcMethodNotFoundError()
    {
        var response = await RunServerAsync(Request(8, "does/not/exist"));

        AssertJsonRpcError(response, 8, -32601);
    }

    [Theory(Timeout = 10_000)]
    [MemberData(nameof(InvalidRequestEnvelopes))]
    public async Task InvalidRequestEnvelopeReturnsInvalidRequestWithoutExecution(string request)
    {
        var client = new ScriptedDapClient();
        await using var session = CreateSession(client);
        var before = JsonSerializer.SerializeToNode(session.GetStatus());

        var response = await RunServerAsync(session, request);

        AssertJsonRpcError(response, null, -32600);
        Assert.True(JsonNode.DeepEquals(before, JsonSerializer.SerializeToNode(session.GetStatus())));
        Assert.Equal(0, client.CreateCount);
    }

    [Theory(Timeout = 10_000)]
    [InlineData("null")]
    [InlineData("1.5")]
    public async Task InvalidRequestIdReturnsInvalidRequestWithoutExecutingTool(string requestId)
    {
        var client = new ScriptedDapClient();
        await using var session = CreateSession(client);
        var before = JsonSerializer.SerializeToNode(session.GetStatus());
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(requestId),
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = "add_breakpoint",
                ["arguments"] = new JsonObject
                {
                    ["file"] = "test.cs",
                    ["line"] = 1
                }
            }
        }.ToJsonString();

        var response = await RunServerAsync(session, request);

        AssertJsonRpcError(response, null, -32600);
        Assert.True(JsonNode.DeepEquals(before, JsonSerializer.SerializeToNode(session.GetStatus())));
        Assert.Equal(0, client.CreateCount);
    }

    [Theory(Timeout = 10_000)]
    [MemberData(nameof(InvalidToolCalls))]
    public async Task InvalidToolCallReturnsInvalidParamsWithoutMutatingSession(string request)
    {
        var client = new ScriptedDapClient();
        await using var session = CreateSession(client);
        var before = JsonSerializer.SerializeToNode(session.GetStatus());

        var response = await RunServerAsync(session, request);

        AssertJsonRpcError(response, 20, -32602);
        Assert.True(JsonNode.DeepEquals(before, JsonSerializer.SerializeToNode(session.GetStatus())));
        Assert.Equal(0, client.CreateCount);
    }

    [Fact(Timeout = 10_000)]
    public async Task CancelNotificationCancelsActiveDebuggerTool()
    {
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = CreateHangingLaunchClient(cancellationObserved);
        await using var session = CreateSession(client);
        var (server, input, output) = StartServer(session);
        input.WriteLine(CallTool(30, "start_debug", new JsonObject
        {
            ["program"] = Path.Combine(Path.GetTempPath(), "app.dll")
        }).ToJsonString());
        await client.WaitForRequestAsync("launch", TestTimeout);

        input.WriteLine(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/cancelled",
            ["params"] = new JsonObject { ["requestId"] = 30 }
        }.ToJsonString());
        await cancellationObserved.Task.WaitAsync(TestTimeout);
        var response = ParseResponse(await output.ReadLineAsync(TestTimeout));
        input.Complete();
        await server.WaitAsync(TestTimeout);

        AssertJsonRpcError(response, 30, -32800);
    }

    [Fact(Timeout = 10_000)]
    public async Task EofCancelsPendingRequestAndRunAsyncFinishesPromptly()
    {
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = CreateHangingLaunchClient(cancellationObserved);
        await using var session = CreateSession(client);
        var (server, input, output) = StartServer(session);
        input.WriteLine(CallTool(31, "start_debug", new JsonObject
        {
            ["program"] = Path.Combine(Path.GetTempPath(), "app.dll")
        }).ToJsonString());
        await client.WaitForRequestAsync("launch", TestTimeout);

        input.Complete();
        await server.WaitAsync(TimeSpan.FromSeconds(1));
        await cancellationObserved.Task.WaitAsync(TestTimeout);
        var response = ParseResponse(await output.ReadLineAsync(TestTimeout));

        AssertJsonRpcError(response, 31, -32800);
    }

    [Fact(Timeout = 10_000)]
    public async Task EofRacingCompletedRequestsDoesNotCancelDisposedSources()
    {
        var runs = Enumerable.Range(0, 500).Select(async iteration =>
        {
            await using var session = new DebugSession(
                () => new BackendInfo { Path = "/test/netcoredbg" },
                new UnexpectedDapClientFactory());
            var (server, input, output) = StartServer(session);
            input.WriteLine(Request(iteration, "ping").ToJsonString());
            input.Complete();

            await server.WaitAsync(TestTimeout);
            AssertJsonRpcResult(
                ParseResponse(await output.ReadLineAsync(TestTimeout)),
                iteration);
        });

        await Task.WhenAll(runs);
    }

    [Fact(Timeout = 10_000)]
    public async Task ExceptionStopPrioritizesExceptionInfoBeforeStack()
    {
        var (session, _) = await CreateStoppedSessionAsync("exception");
        await using (session)
        {
            var response = await RunServerAsync(session, CallTool(40, "get_status").ToJsonString());

            AssertNextActions(
                AssertSuccessfulToolResult(response, 40),
                "get_exception_info",
                "get_call_stack",
                "step_over",
                "step_into",
                "step_out",
                "continue_execution",
                "stop_debug");
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task StackThenScopesExposeDependencyOrderedNextActions()
    {
        var (session, _) = await CreateStoppedSessionAsync("breakpoint");
        await using (session)
        {
            var statusResponse = await RunServerAsync(session, CallTool(41, "get_status").ToJsonString());
            AssertNextActions(
                AssertSuccessfulToolResult(statusResponse, 41),
                "get_call_stack",
                "step_over",
                "step_into",
                "step_out",
                "continue_execution",
                "stop_debug");

            var stackResponse = await RunServerAsync(session, CallTool(42, "get_call_stack").ToJsonString());
            AssertNextActions(
                AssertSuccessfulToolResult(stackResponse, 42),
                "get_scopes",
                "evaluate_expression",
                "step_over",
                "step_into",
                "step_out",
                "continue_execution",
                "stop_debug");

            var scopesResponse = await RunServerAsync(session, CallTool(43, "get_scopes", new JsonObject
            {
                ["frameId"] = 10
            }).ToJsonString());
            AssertNextActions(
                AssertSuccessfulToolResult(scopesResponse, 43),
                "get_variables",
                "evaluate_expression",
                "step_over",
                "step_into",
                "step_out",
                "continue_execution",
                "stop_debug");
        }
    }

    private static async Task<JsonObject> RunServerAsync(
        JsonObject request,
        BackendInfo? backend = null)
    {
        backend ??= new BackendInfo { Path = "/test/netcoredbg" };
        await using var session = new DebugSession(
            () => backend,
            new UnexpectedDapClientFactory());
        return await RunServerAsync(session, request.ToJsonString());
    }

    private static async Task<JsonObject> RunServerAsync(DebugSession session, string request)
    {
        var (server, input, output) = StartServer(session);
        input.WriteLine(request);
        var response = ParseResponse(await output.ReadLineAsync(TestTimeout));
        input.Complete();
        await server.WaitAsync(TestTimeout);
        return response;
    }

    private static (Task Server, TestLineReader Input, TestLineWriter Output) StartServer(DebugSession session)
    {
        var input = new TestLineReader();
        var output = new TestLineWriter();
        return (new McpServer(session, input, output).RunAsync(), input, output);
    }

    private static DebugSession CreateSession(ScriptedDapClient client) =>
        new(
            () => new BackendInfo { Path = "/fake/netcoredbg" },
            new ScriptedDapClientFactory(client));

    private static ScriptedDapClient CreateHangingLaunchClient(
        TaskCompletionSource cancellationObserved)
    {
        var client = new ScriptedDapClient();
        client.OnRequest = async (request, cancellationToken) =>
        {
            if (request.Command != "launch")
            {
                return ScriptedDapClient.Success(request.Command);
            }

            client.EmitInitialized();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new Xunit.Sdk.XunitException("The hanging launch unexpectedly completed.");
            }
            finally
            {
                cancellationObserved.TrySetResult();
            }
        };
        return client;
    }

    private static async Task<(DebugSession Session, ScriptedDapClient Client)> CreateStoppedSessionAsync(
        string reason)
    {
        var client = new ScriptedDapClient
        {
            OnStart = dap =>
            {
                dap.EmitInitialized();
                dap.EmitStopped(reason);
            }
        };
        client.OnRequest = (request, _) => Task.FromResult(
            request.Command switch
            {
                "stackTrace" => ScriptedDapClient.Success("stackTrace", new JsonObject
                {
                    ["stackFrames"] = new JsonArray
                    {
                        new JsonObject { ["id"] = 10, ["name"] = "Program.Main", ["line"] = 1 }
                    },
                    ["totalFrames"] = 1
                }),
                "scopes" => ScriptedDapClient.Success("scopes", new JsonObject
                {
                    ["scopes"] = new JsonArray
                    {
                        new JsonObject { ["name"] = "Locals", ["variablesReference"] = 20 }
                    }
                }),
                _ => ScriptedDapClient.Success(request.Command)
            });
        var session = CreateSession(client);
        await session.EnsureStartedAsync().WaitAsync(TestTimeout);
        Assert.Equal("stopped", session.State);
        return (session, client);
    }

    private static JsonObject InitializeRequest(int id) =>
        Request(id, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "csdbg-tests",
                ["version"] = "1.0.0"
            }
        });

    private static JsonObject Request(int id, string method) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method
        };

    private static JsonObject Request(int id, string method, JsonNode? parameters)
    {
        var request = Request(id, method);
        request["params"] = parameters;
        return request;
    }

    private static JsonObject CallTool(int id, string name) =>
        Request(id, "tools/call", new JsonObject { ["name"] = name });

    private static JsonObject CallTool(int id, string name, JsonNode? arguments) =>
        Request(id, "tools/call", new JsonObject
        {
            ["name"] = name,
            ["arguments"] = arguments
        });

    private static JsonObject ParseResponse(string line) => JsonNode.Parse(line)!.AsObject();

    private static void AssertJsonRpcResult(JsonObject response, int expectedId)
    {
        Assert.Equal("2.0", response["jsonrpc"]!.GetValue<string>());
        Assert.Equal(expectedId, response["id"]!.GetValue<int>());
        Assert.Null(response["error"]);
        Assert.NotNull(response["result"]);
    }

    private static void AssertJsonRpcError(JsonObject response, int? expectedId, int expectedCode)
    {
        Assert.Equal("2.0", response["jsonrpc"]!.GetValue<string>());
        if (expectedId is null)
        {
            Assert.Null(response["id"]);
        }
        else
        {
            Assert.Equal(expectedId, response["id"]!.GetValue<int>());
        }

        Assert.Null(response["result"]);
        Assert.Equal(expectedCode, response["error"]!["code"]!.GetValue<int>());
    }

    private static JsonObject AssertSuccessfulToolResult(JsonObject response, int expectedId)
    {
        AssertJsonRpcResult(response, expectedId);
        var result = response["result"]!.AsObject();
        Assert.False(result["isError"]?.GetValue<bool>() ?? false);
        return ParseTextContent(result);
    }

    private static void AssertToolError(JsonObject response, int expectedId, string expectedCode)
    {
        AssertJsonRpcResult(response, expectedId);
        var result = response["result"]!.AsObject();
        Assert.True(result["isError"]!.GetValue<bool>());
        var text = ParseTextContent(result);
        Assert.Equal(expectedCode, text["error"]!["code"]!.GetValue<string>());
    }

    private static JsonObject ParseTextContent(JsonObject result)
    {
        var content = result["content"]!.AsArray();
        var item = Assert.Single(content)!.AsObject();
        Assert.Equal("text", item["type"]!.GetValue<string>());
        return JsonNode.Parse(item["text"]!.GetValue<string>())!.AsObject();
    }

    private static void AssertEnvelopeRoot(JsonObject envelope)
    {
        var keys = envelope.Select(property => property.Key).Order().ToArray();
        Assert.Equal(["data", "nextActions", "state"], keys);
    }

    private static void AssertNextActions(JsonObject envelope, params string[] expected) =>
        Assert.Equal(
            expected,
            envelope["nextActions"]!.AsArray().Select(item => item!.GetValue<string>()));

    private sealed class UnexpectedDapClientFactory : IDapClientFactory
    {
        public IDapClient Create(string netcoredbgPath) =>
            throw new Xunit.Sdk.XunitException("The test unexpectedly attempted to create a DAP client.");
    }
}
