using System.Text.Json.Nodes;
using Csdbg.Core;
using Csdbg.Core.Dap;

namespace Csdbg.Mcp.Tests;

public sealed class McpServerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    [Fact(Timeout = 10_000)]
    public async Task InitializeReturnsJsonRpcResult()
    {
        var response = await RunServerAsync(Request(1, "initialize"));

        AssertJsonRpcResult(response, 1);
        var result = response["result"]!.AsObject();
        Assert.Equal("2025-06-18", result["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]);
        Assert.NotNull(result["serverInfo"]);
    }

    [Fact(Timeout = 10_000)]
    public async Task GetStatusReturnsNormalizedTextEnvelope()
    {
        var response = await RunServerAsync(CallTool(2, "get_status"));

        var envelope = AssertSuccessfulToolResult(response, 2);
        AssertEnvelopeRoot(envelope);
        Assert.Equal("idle", envelope["state"]!.GetValue<string>());
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
    public async Task MissingToolArgumentsReturnToolError()
    {
        var response = await RunServerAsync(CallTool(6, "add_breakpoint"));

        AssertToolError(response, 6, "invalid_arguments");
    }

    [Fact(Timeout = 10_000)]
    public async Task MalformedToolArgumentsReturnToolError()
    {
        var arguments = new JsonObject
        {
            ["file"] = string.Empty,
            ["line"] = 12
        };

        var response = await RunServerAsync(CallTool(7, "add_breakpoint", arguments));

        AssertToolError(response, 7, "invalid_arguments");
    }

    [Fact(Timeout = 10_000)]
    public async Task UnknownMethodReturnsJsonRpcMethodNotFoundError()
    {
        var response = await RunServerAsync(Request(8, "does/not/exist"));

        Assert.Equal("2.0", response["jsonrpc"]!.GetValue<string>());
        Assert.Equal(8, response["id"]!.GetValue<int>());
        Assert.Null(response["result"]);
        Assert.Equal(-32601, response["error"]!["code"]!.GetValue<int>());
    }

    [Fact(Timeout = 10_000)]
    public async Task StringParamsReturnJsonRpcInvalidParamsError()
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 9,
            ["method"] = "tools/call",
            ["params"] = "not-an-object"
        };

        var response = await RunServerAsync(request);

        Assert.Equal("2.0", response["jsonrpc"]!.GetValue<string>());
        Assert.Equal(9, response["id"]!.GetValue<int>());
        Assert.Null(response["result"]);
        Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
    }

    private static async Task<JsonObject> RunServerAsync(
        JsonObject request,
        BackendInfo? backend = null)
    {
        backend ??= new BackendInfo { Path = "/test/netcoredbg" };
        await using var session = new DebugSession(
            () => backend,
            new UnexpectedDapClientFactory());
        using var input = new StringReader(request.ToJsonString() + Environment.NewLine);
        using var output = new StringWriter();
        var server = new McpServer(session, input, output);

        await server.RunAsync().WaitAsync(TestTimeout);

        var responseLines = output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var responseLine = Assert.Single(responseLines);
        return JsonNode.Parse(responseLine)!.AsObject();
    }

    private static JsonObject Request(int id, string method, JsonObject? parameters = null)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method
        };
        if (parameters is not null)
        {
            request["params"] = parameters;
        }

        return request;
    }

    private static JsonObject CallTool(int id, string name, JsonObject? arguments = null)
    {
        var parameters = new JsonObject { ["name"] = name };
        if (arguments is not null)
        {
            parameters["arguments"] = arguments;
        }

        return Request(id, "tools/call", parameters);
    }

    private static void AssertJsonRpcResult(JsonObject response, int expectedId)
    {
        Assert.Equal("2.0", response["jsonrpc"]!.GetValue<string>());
        Assert.Equal(expectedId, response["id"]!.GetValue<int>());
        Assert.Null(response["error"]);
        Assert.NotNull(response["result"]);
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

    private sealed class UnexpectedDapClientFactory : IDapClientFactory
    {
        public IDapClient Create(string netcoredbgPath) =>
            throw new Xunit.Sdk.XunitException("The test unexpectedly attempted to create a DAP client.");
    }
}
