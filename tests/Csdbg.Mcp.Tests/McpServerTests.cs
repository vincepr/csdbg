using System.Reflection;
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
        "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":\"get_status\",\"_meta\":[]}}",
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
        "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":\"wait_for_stop\",\"arguments\":{\"timeoutMs\":0}}}",
        "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":\"wait_for_stop\",\"arguments\":{\"timeoutMs\":-1}}}",
        "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":\"set_exception_breakpoints\",\"arguments\":{\"filters\":\"all\"}}}"
    };

    [Fact(Timeout = 10_000)]
    [Trait("Description", "Focused evaluation avoids mandatory DAP traversal.")]
    public async Task InitializeReturnsJsonRpcResult()
    {
        var response = await RunServerAsync(InitializeRequest(1));

        AssertJsonRpcResult(response, 1);
        var result = response["result"]!.AsObject();
        Assert.Equal("2025-06-18", result["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]);
        Assert.Equal("csdbg", result["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("0.2.1", result["serverInfo"]!["version"]!.GetValue<string>());
        var instructions = result["instructions"]!.GetValue<string>();
        Assert.Contains("get_status", instructions, StringComparison.Ordinal);
        Assert.Contains(
            "prefer focused evaluate_expression against the current/top frame",
            instructions,
            StringComparison.Ordinal);
        Assert.Contains(
            "only when focused evaluation is insufficient or a different frame is needed",
            instructions,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "inspect the call stack, scopes, and variables before evaluating expressions",
            instructions,
            StringComparison.Ordinal);
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
    [Trait(
        "Description",
        "Execution responses omit unchanged inventory because get_status owns full metadata.")]
    public async Task GetStatusWithoutArgumentsReturnsNormalizedTextEnvelope()
    {
        var response = await RunServerAsync(CallTool(2, "get_status"));

        var envelope = AssertSuccessfulToolResult(response, 2);
        AssertEnvelopeRoot(envelope);
        Assert.Equal("idle", envelope["state"]!.GetValue<string>());
        var status = envelope["data"]!.AsObject();
        Assert.NotNull(status["backend"]);
        Assert.NotNull(status["breakpoints"]);
        Assert.NotNull(status["knownThreadIds"]);
        Assert.NotNull(status["dapRunning"]);
        var build = Assert.IsType<JsonObject>(status["build"]);
        Assert.Equal(
            ["packageVersion", "sourceRevision", "sourceRevisionCapability"],
            build.Select(property => property.Key).Order(StringComparer.Ordinal));
        var informationalVersion = typeof(McpServer).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;
        var informationalParts = informationalVersion.Split('+', 2);
        Assert.Matches(
            @"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$",
            build["packageVersion"]!.GetValue<string>());
        Assert.Equal(
            informationalParts[0],
            build["packageVersion"]!.GetValue<string>());
        Assert.Matches("^[0-9a-f]{40}$", build["sourceRevision"]!.GetValue<string>());
        Assert.Equal(
            informationalParts[1].ToLowerInvariant(),
            build["sourceRevision"]!.GetValue<string>());
        Assert.Equal(
            "assembly_metadata",
            build["sourceRevisionCapability"]!.GetValue<string>());
        Assert.True(
            status.ToJsonString().Length <= 700,
            $"Idle get_status data exceeded 700 characters: {status.ToJsonString().Length}.");
    }

    [Fact]
    public void BuildInfoWithoutSourceRevisionReportsUnavailableCapability()
    {
        var build = BuildInfo.FromInformationalVersion("1.2.3");

        Assert.Equal("1.2.3", build.PackageVersion);
        Assert.Null(build.SourceRevision);
        Assert.Equal("unavailable", build.SourceRevisionCapability);
    }

    [Fact]
    public void BuildInfoRejectsNonCommitBuildMetadataAsSourceRevision()
    {
        var build = BuildInfo.FromInformationalVersion("1.2.3+local-build");

        Assert.Equal("1.2.3", build.PackageVersion);
        Assert.Null(build.SourceRevision);
        Assert.Equal("unavailable", build.SourceRevisionCapability);
    }

    [Fact(Timeout = 10_000)]
    [Trait(
        "Description",
        "Workflow belongs in the skill, so successful responses omit routine nextActions.")]
    public async Task RunningStatusOmitsRoutineNextActions()
    {
        var client = new ScriptedDapClient
        {
            OnStart = dap => dap.EmitInitialized()
        };
        await using var session = CreateSession(client);
        await session.LaunchAsync(Path.Combine(Path.GetTempPath(), "app.dll"))
            .WaitAsync(TestTimeout);

        var response = await RunServerAsync(
            session,
            CallTool(3, "get_status").ToJsonString());

        AssertEnvelopeRoot(AssertSuccessfulToolResult(response, 3));
    }

    [Fact(Timeout = 10_000)]
    public async Task GetStatusAcceptsRequestMetadata()
    {
        var request = CallTool(8, "get_status");
        request["params"]!.AsObject()["_meta"] = new JsonObject
        {
            ["progressToken"] = "codex-request"
        };

        var response = await RunServerAsync(request);

        var envelope = AssertSuccessfulToolResult(response, 8);
        Assert.Equal("idle", envelope["state"]!.GetValue<string>());
    }

    [Fact(Timeout = 10_000)]
    public async Task ExecutionTimeoutSchemasRequirePositiveValues()
    {
        var response = await RunServerAsync(Request(12, "tools/list", new JsonObject()));

        AssertJsonRpcResult(response, 12);
        var tools = response["result"]!["tools"]!.AsArray()
            .Select(item => item!.AsObject())
            .ToDictionary(item => item["name"]!.GetValue<string>());
        var executionTools = new[]
        {
            "continue_execution",
            "wait_for_stop",
            "pause_execution",
            "step_over",
            "step_into",
            "step_out"
        };

        foreach (var toolName in executionTools)
        {
            var timeoutSchema = tools[toolName]["inputSchema"]!["properties"]!["timeoutMs"]!;
            Assert.Equal(1, timeoutSchema["minimum"]!.GetValue<int>());
        }
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
    public async Task SuccessfulBreakpointMutationsReturnOnlyTheChangedBreakpointWithinCompactBudget()
    {
        var (session, _) = await CreateStoppedSessionAsync("breakpoint");
        await using (session)
        {
            var addResponse = await RunServerAsync(
                session,
                CallTool(30, "add_breakpoint", new JsonObject
                {
                    ["file"] = Path.Combine(Path.GetTempPath(), "Program.cs"),
                    ["line"] = 12
                }).ToJsonString());
            var addEnvelope = AssertSuccessfulToolResult(addResponse, 30);
            var breakpointId = addEnvelope["data"]!["id"]!.GetValue<string>();

            var removeResponse = await RunServerAsync(
                session,
                CallTool(31, "remove_breakpoint", new JsonObject
                {
                    ["id"] = breakpointId
                }).ToJsonString());
            var removeEnvelope = AssertSuccessfulToolResult(removeResponse, 31);

            AssertCompactMutationResponse(addResponse, addEnvelope, "data");
            AssertCompactMutationResponse(removeResponse, removeEnvelope, "data", "removed");

            var statusResponse = await RunServerAsync(
                session,
                CallTool(32, "get_status", new JsonObject()).ToJsonString());
            var statusEnvelope = AssertSuccessfulToolResult(statusResponse, 32);
            Assert.Equal("stopped", statusEnvelope["state"]!.GetValue<string>());
            Assert.Equal(
                ["backend", "breakpoints", "build", "currentLocation", "currentThreadId",
                    "dapRunning", "exitCode", "knownThreadIds", "recentOutput", "state",
                    "stopReason"],
                statusEnvelope["data"]!.AsObject()
                    .Select(property => property.Key)
                    .Order()
                    .ToArray());
        }
    }

    [Fact(Timeout = 10_000)]
    [Trait("Description", "Errors retain nextActions for recovery guidance.")]
    public async Task ContinueInWrongStateReturnsToolError()
    {
        var response = await RunServerAsync(CallTool(4, "continue_execution", new JsonObject()));

        var error = AssertToolError(response, 4, "wrong_state");
        AssertNextActions(error, "start_debug", "attach_debug", "add_breakpoint", "get_status");
    }

    [Fact(Timeout = 10_000)]
    public async Task WaitForStopInWrongStateReturnsToolError()
    {
        var response = await RunServerAsync(CallTool(10, "wait_for_stop", new JsonObject()));

        AssertToolError(response, 10, "wrong_state");
    }

    [Fact(Timeout = 10_000)]
    public async Task WaitForStopWhenStoppedReturnsImmediately()
    {
        var (session, _) = await CreateStoppedSessionAsync("breakpoint");
        await using (session)
        {
            var response = await RunServerAsync(
                session,
                CallTool(11, "wait_for_stop", new JsonObject()).ToJsonString());

            var envelope = AssertSuccessfulToolResult(response, 11);
            Assert.Equal("stopped", envelope["state"]!.GetValue<string>());
            Assert.False(envelope["data"]!["timedOut"]!.GetValue<bool>());
            Assert.Null(envelope["data"]!["nextActions"]);
            AssertEnvelopeRoot(envelope);
        }
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
    public async Task ExceptionStopStatusOmitsRoutineNextActions()
    {
        var (session, _) = await CreateStoppedSessionAsync("exception");
        await using (session)
        {
            var response = await RunServerAsync(session, CallTool(40, "get_status").ToJsonString());

            AssertEnvelopeRoot(AssertSuccessfulToolResult(response, 40));
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task StackAndScopesOmitRoutineNextActions()
    {
        var (session, _) = await CreateStoppedSessionAsync("breakpoint");
        await using (session)
        {
            var statusResponse = await RunServerAsync(session, CallTool(41, "get_status").ToJsonString());
            AssertEnvelopeRoot(AssertSuccessfulToolResult(statusResponse, 41));

            var stackResponse = await RunServerAsync(session, CallTool(42, "get_call_stack").ToJsonString());
            AssertEnvelopeRoot(AssertSuccessfulToolResult(stackResponse, 42));

            var scopesResponse = await RunServerAsync(session, CallTool(43, "get_scopes", new JsonObject
            {
                ["frameId"] = 10
            }).ToJsonString());
            AssertEnvelopeRoot(AssertSuccessfulToolResult(scopesResponse, 43));
        }
    }

    [Theory(Timeout = 10_000)]
    [InlineData("error CS1733: Expected expression")]
    [InlineData("Fehler: Der Name ist im aktuellen Kontext nicht vorhanden")]
    [Trait(
        "Description",
        "netcoredbg 3.2.0-1092 supplies no structured distinction between syntax, unavailable context, and target evaluation failures.")]
    public async Task EvaluationFailureUsesHonestTypedFallbackIndependentOfBackendMessage(
        string backendMessage)
    {
        var evaluationAttempts = 0;
        var client = new ScriptedDapClient
        {
            OnStart = dap =>
            {
                dap.EmitInitialized();
                dap.EmitStopped("breakpoint");
            }
        };
        client.OnRequest = (request, _) => Task.FromResult(
            request.Command == "evaluate" && evaluationAttempts++ == 0
                ? ScriptedDapClient.Failure("evaluate", backendMessage)
                : EvaluateSuccess(request.Command));
        await using var session = CreateSession(client);
        await session.EnsureStartedAsync().WaitAsync(TestTimeout);
        var (server, input, output) = StartServer(session);

        input.WriteLine(CallTool(50, "evaluate_expression", new JsonObject
        {
            ["expression"] = "candidate"
        }).ToJsonString());
        var error = AssertToolError(
            ParseResponse(await output.ReadLineAsync(TestTimeout)),
            50,
            "evaluation_failed");

        Assert.Equal(
            "The debugger could not classify the evaluation failure.",
            error["error"]!["message"]!.GetValue<string>());
        Assert.Equal(
            backendMessage,
            error["error"]!["details"]!["backendMessage"]!.GetValue<string>());
        Assert.Equal(
            ["unsupported_syntax", "unavailable_context", "target_failure"],
            error["error"]!["details"]!["indistinguishableKinds"]!
                .AsArray()
                .Select(item => item!.GetValue<string>()));

        input.WriteLine(CallTool(51, "evaluate_expression", new JsonObject
        {
            ["expression"] = "recoveryCandidate"
        }).ToJsonString());
        var recovered = AssertSuccessfulToolResult(
            ParseResponse(await output.ReadLineAsync(TestTimeout)),
            51);
        Assert.Equal("recovered", recovered["data"]!["result"]!.GetValue<string>());
        Assert.Equal(2, evaluationAttempts);

        input.Complete();
        await server.WaitAsync(TestTimeout);

        static JsonObject EvaluateSuccess(string command) =>
            command == "evaluate"
                ? ScriptedDapClient.Success("evaluate", new JsonObject
                {
                    ["result"] = "recovered",
                    ["type"] = "string",
                    ["variablesReference"] = 0
                })
                : ScriptedDapClient.Success(command);
    }

    [Theory(Timeout = 10_000)]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task EvaluationFailureWithoutBackendDetailStillReportsCapabilityBoundary(
        string? backendMessage)
    {
        var client = new ScriptedDapClient
        {
            OnStart = dap =>
            {
                dap.EmitInitialized();
                dap.EmitStopped("breakpoint");
            }
        };
        client.OnRequest = (request, _) => Task.FromResult(
            request.Command == "evaluate"
                ? ScriptedDapClient.Failure("evaluate", backendMessage)
                : ScriptedDapClient.Success(request.Command));
        await using var session = CreateSession(client);
        await session.EnsureStartedAsync().WaitAsync(TestTimeout);

        var response = await RunServerAsync(
            session,
            CallTool(58, "evaluate_expression", new JsonObject
            {
                ["expression"] = "candidate"
            }).ToJsonString());
        var error = AssertToolError(response, 58, "evaluation_failed");
        var details = Assert.IsType<JsonObject>(error["error"]!["details"]);

        Assert.Null(details["backendMessage"]);
        Assert.Equal(
            "generic_dap_failure",
            details["classificationSource"]!.GetValue<string>());
        Assert.Equal(
            ["unsupported_syntax", "unavailable_context", "target_failure"],
            details["indistinguishableKinds"]!
                .AsArray()
                .Select(item => item!.GetValue<string>()));
    }

    [Fact(Timeout = 10_000)]
    public async Task EvaluationFailureBoundsBackendDetail()
    {
        var backendMessage = new string('x', 2_000);
        var client = new ScriptedDapClient
        {
            OnStart = dap =>
            {
                dap.EmitInitialized();
                dap.EmitStopped("breakpoint");
            }
        };
        client.OnRequest = (request, _) => Task.FromResult(
            request.Command == "evaluate"
                ? ScriptedDapClient.Failure("evaluate", backendMessage)
                : ScriptedDapClient.Success(request.Command));
        await using var session = CreateSession(client);
        await session.EnsureStartedAsync().WaitAsync(TestTimeout);

        var response = await RunServerAsync(
            session,
            CallTool(52, "evaluate_expression", new JsonObject
            {
                ["expression"] = "candidate"
            }).ToJsonString());
        var error = AssertToolError(response, 52, "evaluation_failed");
        var backendDetail = error["error"]!["details"]!["backendMessage"]!.GetValue<string>();

        Assert.Equal(512, backendDetail.Length);
        Assert.EndsWith("…", backendDetail, StringComparison.Ordinal);
    }

    [Fact(Timeout = 10_000)]
    public async Task EvaluationTimeoutHasStableCodeAndLeavesSessionReusable()
    {
        var evaluationAttempts = 0;
        var client = new ScriptedDapClient
        {
            OnStart = dap =>
            {
                dap.EmitInitialized();
                dap.EmitStopped("breakpoint");
            }
        };
        client.OnRequest = (request, _) =>
            request.Command == "evaluate" && evaluationAttempts++ == 0
                ? Task.FromException<JsonObject>(
                    new TimeoutException("Zeitüberschreitung des Adapters"))
                : Task.FromResult(
                    request.Command == "evaluate"
                        ? ScriptedDapClient.Success("evaluate", new JsonObject
                        {
                            ["result"] = "recovered",
                            ["type"] = "string",
                            ["variablesReference"] = 0
                        })
                        : ScriptedDapClient.Success(request.Command));
        await using var session = CreateSession(client);
        await session.EnsureStartedAsync().WaitAsync(TestTimeout);
        var (server, input, output) = StartServer(session);

        input.WriteLine(CallTool(53, "evaluate_expression", new JsonObject
        {
            ["expression"] = "candidate"
        }).ToJsonString());
        var error = AssertToolError(
            ParseResponse(await output.ReadLineAsync(TestTimeout)),
            53,
            "evaluation_timeout");
        Assert.Equal(
            "Expression evaluation timed out.",
            error["error"]!["message"]!.GetValue<string>());

        input.WriteLine(CallTool(54, "evaluate_expression", new JsonObject
        {
            ["expression"] = "recoveryCandidate"
        }).ToJsonString());
        var recovered = AssertSuccessfulToolResult(
            ParseResponse(await output.ReadLineAsync(TestTimeout)),
            54);
        Assert.Equal("recovered", recovered["data"]!["result"]!.GetValue<string>());
        Assert.Equal(2, evaluationAttempts);

        input.Complete();
        await server.WaitAsync(TestTimeout);
    }

    [Fact(Timeout = 10_000)]
    public async Task EvaluationAgainstObservedRetiredFrameHasStableCodeWithoutCallingAdapter()
    {
        var currentFrameId = 10;
        var evaluationRequests = 0;
        var client = new ScriptedDapClient
        {
            OnStart = dap =>
            {
                dap.EmitInitialized();
                dap.EmitStopped("breakpoint");
            }
        };
        client.OnRequest = (request, _) => Task.FromResult(
            request.Command switch
            {
                "stackTrace" => ScriptedDapClient.Success("stackTrace", new JsonObject
                {
                    ["stackFrames"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = currentFrameId,
                            ["name"] = "Program.Main",
                            ["line"] = 1
                        }
                    },
                    ["totalFrames"] = 1
                }),
                "evaluate" => EvaluateSuccess(),
                _ => ScriptedDapClient.Success(request.Command)
            });
        await using var session = CreateSession(client);
        await session.EnsureStartedAsync().WaitAsync(TestTimeout);
        await session.GetCallStackAsync(levels: 20).WaitAsync(TestTimeout);

        client.EmitEvent("continued", new JsonObject
        {
            ["threadId"] = 1,
            ["allThreadsContinued"] = true
        });
        currentFrameId = 20;
        client.EmitStopped("breakpoint");
        await session.GetCallStackAsync(levels: 20).WaitAsync(TestTimeout);

        var (server, input, output) = StartServer(session);
        input.WriteLine(CallTool(55, "evaluate_expression", new JsonObject
        {
            ["expression"] = "candidate",
            ["frameId"] = 10
        }).ToJsonString());
        var evaluationResponse = ParseResponse(await output.ReadLineAsync(TestTimeout));
        Assert.True(
            evaluationResponse["result"]!["isError"]?.GetValue<bool>() ?? false,
            evaluationResponse.ToJsonString());
        var error = AssertToolError(evaluationResponse, 55, "stale_frame");

        Assert.Equal(
            "The requested stack frame is no longer available. Refresh the call stack and retry.",
            error["error"]!["message"]!.GetValue<string>());
        Assert.Equal(0, evaluationRequests);

        input.WriteLine(CallTool(56, "evaluate_expression", new JsonObject
        {
            ["expression"] = "recoveryCandidate",
            ["frameId"] = 20
        }).ToJsonString());
        Assert.Equal(
            "1",
            AssertSuccessfulToolResult(
                ParseResponse(await output.ReadLineAsync(TestTimeout)),
                56)["data"]!["result"]!.GetValue<string>());
        Assert.Equal(1, evaluationRequests);

        input.Complete();
        await server.WaitAsync(TestTimeout);

        JsonObject EvaluateSuccess()
        {
            evaluationRequests++;
            return ScriptedDapClient.Success("evaluate", new JsonObject
            {
                ["result"] = "1",
                ["type"] = "int",
                ["variablesReference"] = 0
            });
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task CompleteStackForAnotherThreadDoesNotMisclassifyRetiredFrame()
    {
        var currentFrameId = 10;
        var evaluationRequests = 0;
        var client = new ScriptedDapClient
        {
            OnStart = dap =>
            {
                dap.EmitInitialized();
                dap.EmitStopped("breakpoint", threadId: 1);
            }
        };
        client.OnRequest = (request, _) => Task.FromResult(
            request.Command switch
            {
                "stackTrace" => ScriptedDapClient.Success("stackTrace", new JsonObject
                {
                    ["stackFrames"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = currentFrameId,
                            ["name"] = "Program.Main",
                            ["line"] = 1
                        }
                    },
                    ["totalFrames"] = 1
                }),
                "evaluate" => EvaluateSuccess(),
                _ => ScriptedDapClient.Success(request.Command)
            });
        await using var session = CreateSession(client);
        await session.EnsureStartedAsync().WaitAsync(TestTimeout);
        await session.GetCallStackAsync(threadId: 1, levels: 20).WaitAsync(TestTimeout);

        client.EmitEvent("continued", new JsonObject
        {
            ["threadId"] = 1,
            ["allThreadsContinued"] = true
        });
        currentFrameId = 20;
        client.EmitStopped("breakpoint", threadId: 2);
        await session.GetCallStackAsync(threadId: 2, levels: 20).WaitAsync(TestTimeout);

        var response = await RunServerAsync(
            session,
            CallTool(57, "evaluate_expression", new JsonObject
            {
                ["expression"] = "candidate",
                ["frameId"] = 10
            }).ToJsonString());

        AssertSuccessfulToolResult(response, 57);
        Assert.Equal(1, evaluationRequests);

        JsonObject EvaluateSuccess()
        {
            evaluationRequests++;
            return ScriptedDapClient.Success("evaluate", new JsonObject
            {
                ["result"] = "1",
                ["type"] = "int",
                ["variablesReference"] = 0
            });
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

    private static JsonObject AssertToolError(JsonObject response, int expectedId, string expectedCode)
    {
        AssertJsonRpcResult(response, expectedId);
        var result = response["result"]!.AsObject();
        Assert.True(result["isError"]!.GetValue<bool>());
        var text = ParseTextContent(result);
        Assert.Equal(expectedCode, text["error"]!["code"]!.GetValue<string>());
        return text;
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
        Assert.Equal(["data", "state"], keys);
    }

    private static void AssertCompactMutationResponse(
        JsonObject response,
        JsonObject envelope,
        params string[] breakpointPath)
    {
        var envelopeCharacters = envelope.ToJsonString().Length;
        var responseCharacters = response.ToJsonString().Length;
        Assert.True(
            envelopeCharacters < 512 && responseCharacters < 768,
            $"Breakpoint mutation response exceeded the compact budget: envelope={envelopeCharacters}, raw={responseCharacters}. Response: {response}");
        AssertEnvelopeRoot(envelope);

        JsonNode breakpoint = envelope;
        foreach (var segment in breakpointPath)
        {
            breakpoint = breakpoint[segment]!;
        }

        Assert.Equal(
            ["adapterId", "condition", "file", "id", "line", "message", "requestedLine", "verified"],
            breakpoint.AsObject().Select(property => property.Key).Order().ToArray());
        if (breakpointPath.Length > 1)
        {
            Assert.Equal(["removed"], envelope["data"]!.AsObject()
                .Select(property => property.Key)
                .Order()
                .ToArray());
        }
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
