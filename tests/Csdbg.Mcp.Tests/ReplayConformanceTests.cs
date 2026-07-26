using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Csdbg.Core;
using Csdbg.Core.Dap;

namespace Csdbg.Mcp.Tests;

public sealed class ReplayConformanceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task SchedulerReplayMatchesTheMeasuredNetcoredbgWorkflow()
    {
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(FixturePath))!.AsObject();
        var steps = fixture["steps"]!.AsArray().Select(item => item!.AsObject()).ToArray();

        Assert.Equal(
            [
                "get_status",
                "add_breakpoint",
                "add_breakpoint",
                "start_debug",
                "wait_for_stop",
                "evaluate_expression",
                "get_scopes",
                "continue_execution",
                "get_variables",
                "evaluate_expression",
                "remove_breakpoint",
                "continue_execution",
                "evaluate_expression",
                "continue_execution",
                "stop_debug",
                "start_debug",
                "wait_for_stop",
                "evaluate_expression",
                "stop_debug",
                "remove_breakpoint"
            ],
            steps.Select(step => step["tool"]!.GetValue<string>()));
        Assert.All(
            steps.Where(step => step["tool"]!.GetValue<string>() == "start_debug"),
            step => Assert.False(step["arguments"]!["stopAtEntry"]!.GetValue<bool>()));
        Assert.Equal(
            "order[0].Name + \" -> \" + order[1].Name + \" -> \" + order[2].Name + \" -> \" + order[3].Name + \" -> \" + order[4].Name",
            steps[12]["arguments"]!["expression"]!.GetValue<string>());
    }

    [Fact(Timeout = 15_000)]
    public async Task SchedulerReplayMeetsFocusedWorkflowAndLifecycleBudgets()
    {
        Assert.True(
            File.Exists(FixturePath),
            $"Repository-owned scheduler replay fixture is missing: {FixturePath}");

        var fixtureText = await File.ReadAllTextAsync(FixturePath);
        AssertSanitized(fixtureText);
        var fixture = JsonNode.Parse(fixtureText)!.AsObject();
        Assert.True(
            fixture["recursiveResponseShapeSha256"] is JsonValue,
            "Replay fixture must lock recursive nested response shapes.");
        var dapInteractions = new Queue<JsonObject>(
            fixture["dapInteractions"]!.AsArray().Select(item => item!.AsObject()));
        var dapInteractionIndex = 0;
        var adapterProcesses = new List<Process>();
        var targetProcesses = new List<Process>();
        var factory = new ReplayDapClientFactory(CreateClient);

        await using var session = new DebugSession(
            () => new BackendInfo { Path = "/fixture/netcoredbg" },
            factory);
        var input = new TestLineReader();
        var output = new TestLineWriter();
        var server = new McpServer(session, input, output).RunAsync();
        var nextId = 1;
        var captures = new Dictionary<string, JsonNode>(StringComparer.Ordinal);

        await SendAsync(InitializeRequest(nextId++));
        var toolsResponse = await SendAsync(Request(nextId++, "tools/list", new JsonObject()));
        AssertToolSchemas(fixture, toolsResponse);

        var toolCalls = 0;
        var requestCharacters = 0;
        var rawResponseCharacters = 0;
        var canonicalResponseCharacters = 0;
        var responseShapes = new Dictionary<string, int>(StringComparer.Ordinal);
        var recursiveResponseShapes = new StringBuilder();
        foreach (var stepNode in fixture["steps"]!.AsArray())
        {
            var step = stepNode!.AsObject();
            EmitEvents(step["eventsBefore"]?.AsArray(), factory.Clients.LastOrDefault());
            var arguments = ResolveCaptures(
                step["arguments"]?.DeepClone().AsObject() ?? new JsonObject(),
                captures);
            var request = CallTool(
                nextId++,
                step["tool"]!.GetValue<string>(),
                arguments);
            var requestText = request.ToJsonString();
            input.WriteLine(requestText);
            var responseText = await output.ReadLineAsync(TestTimeout);
            var response = JsonNode.Parse(responseText)!.AsObject();
            toolCalls++;
            requestCharacters += requestText.Length;
            rawResponseCharacters += responseText.Length;
            canonicalResponseCharacters += CanonicalResponseLength(response);

            var envelope = ParseToolEnvelope(response);
            AppendRecursiveShape(
                recursiveResponseShapes,
                step["tool"]!.GetValue<string>(),
                envelope);
            var shape = string.Join(
                ',',
                envelope.Select(property => property.Key).Order(StringComparer.Ordinal));
            responseShapes[shape] = responseShapes.GetValueOrDefault(shape) + 1;
            AssertStep(step, response, envelope);
            CaptureValues(step, envelope, captures);
        }

        input.Complete();
        await server.WaitAsync(TestTimeout);

        Assert.Empty(dapInteractions);
        Assert.All(factory.Clients, replayClient => Assert.False(replayClient.IsRunning));
        Assert.Equal(2, factory.Clients.Count);
        Assert.All(factory.Clients, replayClient =>
        {
            Assert.Equal(1, replayClient.StartCount);
            Assert.Equal(1, replayClient.DisposeCount);
        });
        Assert.Equal(2, adapterProcesses.Count);
        Assert.Equal(2, adapterProcesses.Select(adapter => adapter.Id).Distinct().Count());
        Assert.All(adapterProcesses, adapter => Assert.True(adapter.HasExited));
        Assert.Equal(2, targetProcesses.Count);
        Assert.Equal(2, targetProcesses.Select(target => target.Id).Distinct().Count());
        Assert.All(targetProcesses, target => Assert.True(target.HasExited));
        Assert.Empty(
            adapterProcesses.Select(adapter => adapter.Id)
                .Intersect(targetProcesses.Select(target => target.Id)));
        Assert.Equal("idle", session.State);
        Assert.Equal(fixture["limits"]!["toolCalls"]!.GetValue<int>(), toolCalls);
        var expectedRequestCharacters = fixture["metrics"]!["requestCharacters"]!.GetValue<int>();
        var expectedResponseCharacters =
            fixture["metrics"]!["canonicalResponseCharacters"]!.GetValue<int>();
        Assert.True(
            requestCharacters == expectedRequestCharacters &&
            canonicalResponseCharacters == expectedResponseCharacters,
            $"Recorded request/canonical-response characters were {expectedRequestCharacters}/{expectedResponseCharacters}; replay produced {requestCharacters}/{canonicalResponseCharacters}.");
        Assert.True(
            rawResponseCharacters <= fixture["limits"]!["responseCharacters"]!.GetValue<int>(),
            $"Replay returned {rawResponseCharacters} raw model-visible characters.");
        if (OperatingSystem.IsWindows())
        {
            var expectedRawResponseCharacters =
                fixture["metrics"]!["windowsRawResponseCharacters"]!.GetValue<int>();
            Assert.True(
                rawResponseCharacters == expectedRawResponseCharacters,
                $"Recorded Windows raw response characters were {expectedRawResponseCharacters}; replay produced {rawResponseCharacters}.");
        }

        Assert.Equal(
            ReadExpectedShapes(fixture),
            responseShapes.OrderBy(item => item.Key).ToDictionary());
        var recursiveShapeHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(recursiveResponseShapes.ToString())))
            .ToLowerInvariant();
        var expectedShapeHash =
            fixture["recursiveResponseShapeSha256"]!.GetValue<string>();
        Assert.True(
            string.Equals(expectedShapeHash, recursiveShapeHash, StringComparison.Ordinal),
            $"Recorded recursive response-shape hash was {expectedShapeHash}; replay produced {recursiveShapeHash}.");
        adapterProcesses.ForEach(adapter => adapter.Dispose());
        targetProcesses.ForEach(target => target.Dispose());

        ScriptedDapClient CreateClient()
        {
            Process? adapterProcess = null;
            Process? targetProcess = null;
            var client = new ScriptedDapClient
            {
                OnStart = dap =>
                {
                    adapterProcess = StartReplayProcess("adapter");
                    adapterProcesses.Add(adapterProcess);
                    dap.EmitInitialized();
                },
                OnDispose = () =>
                {
                    StopReplayProcess(targetProcess);
                    StopReplayProcess(adapterProcess);
                }
            };
            client.OnRequest = (request, _) =>
            {
                Assert.NotEmpty(dapInteractions);
                var interaction = dapInteractions.Dequeue();
                dapInteractionIndex++;
                var expectedCommand = interaction["command"]!.GetValue<string>();
                Assert.True(
                    string.Equals(expectedCommand, request.Command, StringComparison.Ordinal),
                    $"DAP interaction {dapInteractionIndex} expected '{expectedCommand}' but received '{request.Command}' with {request.Arguments?.ToJsonString()}.");
                if (request.Command == "launch")
                {
                    Assert.Null(targetProcess);
                    targetProcess = StartReplayProcess("target");
                    targetProcesses.Add(targetProcess);
                }
                else if (request.Command == "disconnect")
                {
                    StopReplayProcess(targetProcess);
                }

                var response = new JsonObject
                {
                    ["type"] = "response",
                    ["command"] = request.Command,
                    ["success"] = interaction["success"]?.GetValue<bool>() ?? true,
                    ["body"] = interaction["body"]?.DeepClone() ?? new JsonObject()
                };
                if (interaction["message"] is not null)
                {
                    response["message"] = interaction["message"]!.GetValue<string>();
                }

                EmitEvents(interaction["events"]?.AsArray(), client);

                return Task.FromResult(response);
            };
            return client;
        }

        async Task<JsonObject> SendAsync(JsonObject request)
        {
            input.WriteLine(request.ToJsonString());
            return JsonNode.Parse(await output.ReadLineAsync(TestTimeout))!.AsObject();
        }
    }

    private static Process StartReplayProcess(string role)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(
            Path.Combine(AppContext.BaseDirectory, "Csdbg.Mcp.Tests.runtimeconfig.json"));
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "SchedulerReplay.dll"));
        startInfo.ArgumentList.Add("--wait-for-cleanup");
        startInfo.ArgumentList.Add(role);

        var process = Process.Start(startInfo)
            ?? throw new Xunit.Sdk.XunitException("Failed to start replay process.");
        try
        {
            var ready = process.StandardOutput.ReadLineAsync()
                .WaitAsync(TestTimeout)
                .GetAwaiter()
                .GetResult();
            Assert.Equal($"ready:{role}", ready);
            Assert.False(process.HasExited);
            return process;
        }
        catch
        {
            StopReplayProcess(process);
            process.Dispose();
            throw;
        }
    }

    private static void StopReplayProcess(Process? process)
    {
        if (process is null || process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
        Assert.True(process.WaitForExit(milliseconds: 5_000));
    }

    private static void EmitEvents(JsonArray? events, ScriptedDapClient? client)
    {
        foreach (var eventNode in events ?? [])
        {
            Assert.NotNull(client);
            var replayEvent = eventNode!.AsObject();
            client.EmitEvent(
                replayEvent["event"]!.GetValue<string>(),
                replayEvent["body"]?.DeepClone().AsObject());
        }
    }

    private static void AssertSanitized(string fixtureText)
    {
        var forbidden = new[]
        {
            "authorization:",
            "connectionstring",
            "password=",
            "private key",
            @"D:\coding",
            @"C:\Users"
        };
        foreach (var value in forbidden)
        {
            Assert.DoesNotContain(value, fixtureText, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertToolSchemas(JsonObject fixture, JsonObject response)
    {
        var tools = response["result"]!["tools"]!.AsArray();
        Assert.Equal(fixture["toolSchemas"]!["count"]!.GetValue<int>(), tools.Count);
        var json = tools.ToJsonString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();
        Assert.Equal(fixture["toolSchemas"]!["sha256"]!.GetValue<string>(), hash);
    }

    private static int CanonicalResponseLength(JsonObject response)
    {
        var canonical = response.DeepClone().AsObject();
        var textNode = canonical["result"]?["content"]?[0]?["text"];
        if (textNode is null)
        {
            return canonical.ToJsonString().Length;
        }

        var envelope = JsonNode.Parse(textNode.GetValue<string>())!;
        CanonicalizePaths(envelope);
        canonical["result"]!["content"]![0]!["text"] = envelope.ToJsonString();
        return canonical.ToJsonString().Length;
    }

    private static void AppendRecursiveShape(
        StringBuilder builder,
        string tool,
        JsonNode envelope)
    {
        builder.Append(tool).Append(':');
        AppendNodeShape(builder, "$", envelope);
        builder.AppendLine();
    }

    private static void AppendNodeShape(StringBuilder builder, string path, JsonNode? node)
    {
        if (node is null)
        {
            builder.Append(path).Append("=null;");
            return;
        }

        if (node is JsonObject jsonObject)
        {
            builder.Append(path).Append("=object;");
            foreach (var property in jsonObject.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                AppendNodeShape(builder, $"{path}/{property.Key}", property.Value);
            }

            return;
        }

        if (node is JsonArray jsonArray)
        {
            builder.Append(path).Append("=array[").Append(jsonArray.Count).Append("];");
            for (var index = 0; index < jsonArray.Count; index++)
            {
                AppendNodeShape(builder, $"{path}/{index}", jsonArray[index]);
            }

            return;
        }

        var kind = JsonSerializer.SerializeToElement(node).ValueKind;
        builder.Append(path).Append('=').Append(kind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            _ => kind.ToString().ToLowerInvariant()
        }).Append(';');
    }

    private static void CanonicalizePaths(JsonNode node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject.ToArray())
            {
                if (property.Value is JsonValue value &&
                    value.TryGetValue<string>(out var text))
                {
                    jsonObject[property.Key] = CanonicalizePath(text);
                }
                else if (property.Value is not null)
                {
                    CanonicalizePaths(property.Value);
                }
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray)
            {
                if (item is not null)
                {
                    CanonicalizePaths(item);
                }
            }
        }
    }

    private static string CanonicalizePath(string value)
    {
        var fixturePaths = new[]
        {
            "/csdbg-fixtures/SchedulerReplay/TaskResolver.cs",
            "/csdbg-fixtures/SchedulerReplay/TaskRunner.cs",
            "/csdbg-fixtures/SchedulerReplay.dll",
            "/csdbg-fixtures"
        };
        foreach (var fixturePath in fixturePaths)
        {
            value = value.Replace(
                Path.GetFullPath(fixturePath),
                fixturePath,
                StringComparison.Ordinal);
        }

        return value;
    }

    private static JsonObject ResolveCaptures(
        JsonObject arguments,
        IReadOnlyDictionary<string, JsonNode> captures)
    {
        foreach (var property in arguments.ToArray())
        {
            if (property.Value is JsonValue value &&
                value.TryGetValue<string>(out var text) &&
                text.StartsWith('$'))
            {
                arguments[property.Key] = captures[text[1..]].DeepClone();
            }
        }

        return arguments;
    }

    private static void AssertStep(JsonObject step, JsonObject response, JsonObject envelope)
    {
        Assert.Equal("2.0", response["jsonrpc"]!.GetValue<string>());
        Assert.NotNull(response["result"]);
        var expected = step["expect"]!.AsObject();
        var expectedState = expected["state"]!.GetValue<string>();
        var actualState = envelope["state"]!.GetValue<string>();
        Assert.True(
            string.Equals(expectedState, actualState, StringComparison.Ordinal),
            $"Tool '{step["tool"]!.GetValue<string>()}' expected state '{expectedState}' but returned '{actualState}': {envelope.ToJsonString()}");
        var expectedError = expected["errorCode"]?.GetValue<string>();
        if (expectedError is null)
        {
            Assert.False(response["result"]!["isError"]?.GetValue<bool>() ?? false);
        }
        else
        {
            Assert.True(response["result"]!["isError"]!.GetValue<bool>());
            Assert.Equal(expectedError, envelope["error"]!["code"]!.GetValue<string>());
        }

        foreach (var assertionNode in expected["values"]?.AsArray() ?? [])
        {
            var assertion = assertionNode!.AsObject();
            var actual = Select(envelope, assertion["path"]!.GetValue<string>());
            Assert.Equal(assertion["value"]!.ToJsonString(), actual.ToJsonString());
        }
    }

    private static void CaptureValues(
        JsonObject step,
        JsonObject envelope,
        IDictionary<string, JsonNode> captures)
    {
        foreach (var captureNode in step["capture"]?.AsArray() ?? [])
        {
            var capture = captureNode!.AsObject();
            captures[capture["name"]!.GetValue<string>()] =
                Select(envelope, capture["path"]!.GetValue<string>()).DeepClone();
        }
    }

    private static JsonNode Select(JsonNode root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current switch
            {
                JsonObject jsonObject => jsonObject[segment]!,
                JsonArray jsonArray => jsonArray[int.Parse(segment, System.Globalization.CultureInfo.InvariantCulture)]!,
                _ => throw new Xunit.Sdk.XunitException($"Cannot select '{path}'.")
            };
        }

        return current;
    }

    private static Dictionary<string, int> ReadExpectedShapes(JsonObject fixture) =>
        fixture["responseShapes"]!.AsObject()
            .ToDictionary(
                property => property.Key,
                property => property.Value!.GetValue<int>(),
                StringComparer.Ordinal);

    private static JsonObject ParseToolEnvelope(JsonObject response)
    {
        var content = response["result"]!["content"]!.AsArray();
        var item = Assert.Single(content)!.AsObject();
        Assert.Equal("text", item["type"]!.GetValue<string>());
        return JsonNode.Parse(item["text"]!.GetValue<string>())!.AsObject();
    }

    private static JsonObject InitializeRequest(int id) =>
        Request(id, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "scheduler-replay-conformance",
                ["version"] = "1.0"
            }
        });

    private static JsonObject CallTool(int id, string name, JsonObject arguments) =>
        Request(id, "tools/call", new JsonObject
        {
            ["name"] = name,
            ["arguments"] = arguments
        });

    private static JsonObject Request(int id, string method, JsonNode parameters) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters
        };

    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "scheduler-replay.json");

    private sealed class ReplayDapClientFactory(Func<ScriptedDapClient> create) : IDapClientFactory
    {
        public List<ScriptedDapClient> Clients { get; } = [];

        public IDapClient Create(string netcoredbgPath)
        {
            var client = create();
            Clients.Add(client);
            return client;
        }
    }
}
