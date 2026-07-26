using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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
    public void SchedulerReleaseBuildPreservesDebuggerLocals()
    {
        var assembly = System.Reflection.Assembly.LoadFrom(
            Path.Combine(AppContext.BaseDirectory, "SchedulerReplay.dll"));
        var debugging = assembly.GetCustomAttributes(typeof(DebuggableAttribute), inherit: false)
            .Cast<DebuggableAttribute>()
            .SingleOrDefault();

        Assert.True(
            debugging?.DebuggingFlags.HasFlag(
                DebuggableAttribute.DebuggingModes.DisableOptimizations) is true,
            $"Release SchedulerReplay must disable optimizations; flags were {debugging?.DebuggingFlags.ToString() ?? "<missing>"}.");
    }

    [Fact]
    public void SchedulerReplayHasARepositoryOwnedDapAdapterProcess()
    {
        Assert.True(
            File.Exists(ReplayAdapterPath),
            $"Repository-owned replay DAP adapter is missing: {ReplayAdapterPath}");
    }

    [Fact(Timeout = 15_000)]
    public async Task ReplayAdapterAcceptsEarlyCleanupDisconnect()
    {
        using var replayEnvironment = new ReplayEnvironment();
        await using var client = new DapClient(ReplayAdapterPath);

        await client.StartAsync();
        var launch = await client.SendRequestAsync("launch");
        Assert.True(launch["success"]!.GetValue<bool>());

        var disconnect = await client.SendRequestAsync("disconnect");
        Assert.True(disconnect["success"]!.GetValue<bool>());

        var evidence = await replayEnvironment.ReadEvidenceUntilAsync(
            items => items.Any(item => item["kind"]?.GetValue<string>() == "adapter-exit"),
            TestTimeout);
        Assert.Contains(
            evidence,
            item => item["kind"]?.GetValue<string>() == "cleanup-disconnect");
        Assert.Equal(
            EvidencePids(evidence, "target-start").Order(),
            EvidencePids(evidence, "target-exit").Order());
        Assert.DoesNotContain(
            await replayEnvironment.ReadDiagnosticsAsync(),
            item => item["kind"]?.GetValue<string>() is
                "top-level-fault" or "appdomain-unhandled" or "unobserved-task");
        replayEnvironment.MarkSucceeded();
    }

    [Fact]
    public async Task SchedulerReplayRequiresOutOfProcessMcpServerEvidence()
    {
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(FixturePath))!.AsObject();

        Assert.Equal(
            "out-of-process",
            fixture["mcpServer"]?["mode"]?.GetValue<string>());
        Assert.True(
            fixture["mcpServer"]?["requiresPidAndExitEvidence"]?.GetValue<bool>());
    }

    [Fact]
    public async Task LiveProvenanceUsesStableSourceInputsInsteadOfRuntimeHashes()
    {
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(FixturePath))!.AsObject();
        var provenance = fixture["liveProvenance"]!.AsObject();

        Assert.Null(provenance["targetDllSha256"]);
        Assert.Null(provenance["targetPdbSha256"]);
        Assert.Null(provenance["csdbgMcpSha256"]);
        Assert.Null(provenance["csdbgCoreSha256"]);
        Assert.Equal(
            "sorted-repo-path-nul-normalized-utf8-nul-sha256",
            provenance["sourceInputHashAlgorithm"]?.GetValue<string>());
        var sourceInputs = provenance["sourceInputs"]!.AsObject();
        Assert.Equal(
            ["csdbgCore", "csdbgMcp", "replayDapAdapter", "schedulerReplay"],
            sourceInputs.Select(item => item.Key).Order(StringComparer.Ordinal));
        Assert.All(
            sourceInputs,
            item => Assert.Matches("^[0-9a-f]{64}$", item.Value!.GetValue<string>()));
        Assert.Equal(
            "required-when-git-is-available",
            provenance["gitHeadValidation"]?.GetValue<string>());
    }

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
        AssertLiveProvenance(fixture);
        Assert.True(
            fixture["recursiveResponseShapeSha256"] is JsonValue,
            "Replay fixture must lock recursive nested response shapes.");
        using var replayEnvironment = new ReplayEnvironment();
        await using var server = new ReplayMcpProcess(
            ReplayAdapterPath,
            replayEnvironment.DirectoryPath);
        Assert.NotEqual(Environment.ProcessId, server.Id);
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
        var startSession = 0;
        int? pendingStopRelease = null;
        foreach (var stepNode in fixture["steps"]!.AsArray())
        {
            var step = stepNode!.AsObject();
            var arguments = ResolveCaptures(
                step["arguments"]?.DeepClone().AsObject() ?? new JsonObject(),
                captures);
            var request = CallTool(
                nextId++,
                step["tool"]!.GetValue<string>(),
                arguments);
            var requestText = request.ToJsonString();
            string responseText;
            try
            {
                if (step["tool"]!.GetValue<string>() == "wait_for_stop"
                    && pendingStopRelease is { } releaseSession)
                {
                    await server.WriteAsync(requestText);
                    replayEnvironment.ReleaseStop(releaseSession);
                    pendingStopRelease = null;
                    responseText = await server.ReadAsync(TestTimeout);
                }
                else
                {
                    responseText = await server.SendAsync(requestText, TestTimeout);
                }
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException(
                    $"MCP replay timed out during '{step["tool"]!.GetValue<string>()}'.",
                    ex);
            }
            var response = JsonNode.Parse(responseText)!.AsObject();
            if (step["tool"]!.GetValue<string>() == "start_debug")
            {
                pendingStopRelease = startSession++;
            }
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

        var serverResult = await server.CompleteAsync(TestTimeout);
        replayEnvironment.RecordServerLifecycle(
            server.Id,
            serverResult.ExitCode);
        Assert.Equal(0, serverResult.ExitCode);
        Assert.Empty(serverResult.RemainingStandardOutput);
        Assert.True(
            serverResult.StandardError.Length <= 4096,
            $"MCP server stderr exceeded 4096 characters: {serverResult.StandardError.Length}.");
        Assert.Contains(
            "csdbg MCP server running on stdio; waiting for client input.",
            serverResult.StandardError,
            StringComparison.Ordinal);

        var evidence = await replayEnvironment.ReadEvidenceAsync(TestTimeout);
        var adapterPids = EvidencePids(evidence, "adapter-start");
        var adapterExitPids = EvidencePids(evidence, "adapter-exit");
        var targetPids = EvidencePids(evidence, "target-start");
        var targetExitPids = EvidencePids(evidence, "target-exit");
        Assert.Equal(2, adapterPids.Count);
        Assert.Equal(2, adapterPids.Distinct().Count());
        Assert.Equal(adapterPids.Order(), adapterExitPids.Order());
        Assert.Equal(2, targetPids.Count);
        Assert.Equal(2, targetPids.Distinct().Count());
        Assert.Equal(targetPids.Order(), targetExitPids.Order());
        await AssertProcessesExitedAsync(adapterPids.Concat(targetPids), TestTimeout);
        Assert.Empty(
            adapterPids.Intersect(targetPids));
        Assert.DoesNotContain(
            await replayEnvironment.ReadDiagnosticsAsync(),
            item => item["kind"]?.GetValue<string>() is
                "top-level-fault"
                or "appdomain-unhandled"
                or "unobserved-task"
                or "stop-release-fault"
                or "stop-release-timeout"
                or "stop-release-join-timeout");
        var replayedCommands = evidence
            .Where(item => item["kind"]!.GetValue<string>() == "dap-request")
            .Select(item => item["command"]!.GetValue<string>())
            .ToArray();
        Assert.Equal(
            fixture["dapInteractions"]!.AsArray()
                .Select(item => item!["command"]!.GetValue<string>()),
            replayedCommands);
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
        replayEnvironment.MarkSucceeded();

        async Task<JsonObject> SendAsync(JsonObject request)
        {
            var responseText = await server.SendAsync(request.ToJsonString(), TestTimeout);
            return JsonNode.Parse(responseText)!.AsObject();
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

    private static void AssertLiveProvenance(JsonObject fixture)
    {
        var provenance = fixture["liveProvenance"]!.AsObject();
        Assert.Equal("Release", provenance["targetConfiguration"]!.GetValue<string>());
        Assert.Equal(20, provenance["toolCalls"]!.GetValue<int>());
        Assert.True(provenance["requestCharacters"]!.GetValue<int>() < 30_000);
        Assert.True(provenance["responseCharacters"]!.GetValue<int>() < 30_000);
        Assert.Equal(29, provenance["firstStopLine"]!.GetValue<int>());
        Assert.Equal(8, provenance["secondStopLine"]!.GetValue<int>());
        Assert.Equal(
            "debugger_error",
            provenance["staleHandleError"]!.GetValue<string>());
        Assert.Equal(
            "docs -> lint -> build -> test -> deploy",
            provenance["observedOrder"]!.GetValue<string>());
        Assert.Equal(
            "sorted-repo-path-nul-normalized-utf8-nul-sha256",
            provenance["sourceInputHashAlgorithm"]!.GetValue<string>());

        var repositoryRoot = FindRepositoryRoot();
        var sourceInputs = provenance["sourceInputs"]!.AsObject();
        Assert.Equal(
            sourceInputs["csdbgCore"]!.GetValue<string>(),
            SourceInputSha256(repositoryRoot, "src/Csdbg.Core"));
        Assert.Equal(
            sourceInputs["csdbgMcp"]!.GetValue<string>(),
            SourceInputSha256(repositoryRoot, "src/Csdbg.Mcp"));
        Assert.Equal(
            sourceInputs["schedulerReplay"]!.GetValue<string>(),
            SourceInputSha256(repositoryRoot, "integration/SchedulerReplay"));
        Assert.Equal(
            sourceInputs["replayDapAdapter"]!.GetValue<string>(),
            SourceInputSha256(repositoryRoot, "integration/ReplayDapAdapter"));

        AssertBuiltArtifactProvenance(repositoryRoot);
    }

    private static string SourceInputSha256(string repositoryRoot, string relativeRoot)
    {
        var sourceRoot = Path.Combine(
            repositoryRoot,
            relativeRoot.Replace('/', Path.DirectorySeparatorChar));
        var files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path =>
                (path.EndsWith(".cs", StringComparison.Ordinal)
                    || path.EndsWith(".csproj", StringComparison.Ordinal))
                && !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => segment is "bin" or "obj"))
            .Select(path => new
            {
                FullPath = path,
                RelativePath = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/')
            })
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(files);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.RelativePath));
            hash.AppendData([0]);
            var normalized = File.ReadAllText(file.FullPath)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            hash.AppendData(Encoding.UTF8.GetBytes(normalized));
            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AssertBuiltArtifactProvenance(string repositoryRoot)
    {
        var artifacts = new[]
        {
            "Csdbg.Core",
            "Csdbg.Mcp",
            "SchedulerReplay",
            "ReplayDapAdapter"
        };
        var gitHead = TryReadGitHead(repositoryRoot);
        var revisions = new List<string>();
        foreach (var artifact in artifacts)
        {
            var dllPath = Path.Combine(AppContext.BaseDirectory, $"{artifact}.dll");
            var pdbPath = Path.Combine(AppContext.BaseDirectory, $"{artifact}.pdb");
            Assert.True(File.Exists(dllPath), $"Required Release DLL is missing: {dllPath}");
            Assert.True(File.Exists(pdbPath), $"Required Release PDB is missing: {pdbPath}");
            AssertPortablePdbMatches(dllPath, pdbPath);

            var assembly = Assembly.LoadFrom(dllPath);
            var informationalVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            var productVersion = FileVersionInfo.GetVersionInfo(dllPath).ProductVersion;
            Assert.False(
                string.IsNullOrWhiteSpace(informationalVersion),
                $"{artifact} has no informational version.");
            Assert.False(
                string.IsNullOrWhiteSpace(productVersion),
                $"{artifact} has no product version.");
            Assert.Equal(informationalVersion, productVersion);
            revisions.Add(SourceRevision(informationalVersion!));
        }

        Assert.Single(revisions.Distinct(StringComparer.Ordinal));
        if (gitHead.Available)
        {
            Assert.Equal(gitHead.Head, Assert.Single(revisions.Distinct(StringComparer.Ordinal)));
        }
        else
        {
            Assert.False(
                string.IsNullOrWhiteSpace(gitHead.CapabilityReason),
                "Git-unavailable provenance must expose an explicit capability reason.");
        }

        AssertSchedulerSequencePoints(
            Path.Combine(AppContext.BaseDirectory, "SchedulerReplay.pdb"));
    }

    private static void AssertPortablePdbMatches(string dllPath, string pdbPath)
    {
        using var peStream = File.OpenRead(dllPath);
        using var peReader = new PEReader(peStream);
        var codeViewEntries = peReader.ReadDebugDirectory()
            .Where(entry => entry.Type == DebugDirectoryEntryType.CodeView)
            .ToArray();
        var codeView = Assert.Single(codeViewEntries);
        var codeViewData = peReader.ReadCodeViewDebugDirectoryData(codeView);
        Assert.Equal(
            Path.GetFileName(pdbPath),
            Path.GetFileName(codeViewData.Path));

        using var pdbStream = File.OpenRead(pdbPath);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
        var debugMetadataHeader = provider.GetMetadataReader().DebugMetadataHeader;
        Assert.NotNull(debugMetadataHeader);
        var pdbId = debugMetadataHeader.Id;
        Assert.True(pdbId.Length >= 16, $"{pdbPath} has no portable PDB identifier.");
        Assert.Equal(codeViewData.Guid, new Guid(pdbId[..16].ToArray()));
    }

    private static void AssertSchedulerSequencePoints(string pdbPath)
    {
        using var pdbStream = File.OpenRead(pdbPath);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
        var reader = provider.GetMetadataReader();
        var documents = reader.Documents
            .Select(handle => reader.GetString(reader.GetDocument(handle).Name))
            .ToArray();
        Assert.Contains(
            documents,
            path => path.EndsWith("TaskResolver.cs", StringComparison.Ordinal));
        Assert.Contains(
            documents,
            path => path.EndsWith("TaskRunner.cs", StringComparison.Ordinal));

        var sequencePoints = new List<(string Document, int Line)>();
        foreach (var handle in reader.MethodDebugInformation)
        {
            var method = reader.GetMethodDebugInformation(handle);
            foreach (var point in method.GetSequencePoints().Where(point => !point.IsHidden))
            {
                var documentHandle = point.Document.IsNil ? method.Document : point.Document;
                if (!documentHandle.IsNil)
                {
                    sequencePoints.Add((
                        reader.GetString(reader.GetDocument(documentHandle).Name),
                        point.StartLine));
                }
            }
        }

        Assert.Contains(
            sequencePoints,
            point => point.Document.EndsWith("TaskResolver.cs", StringComparison.Ordinal)
                && point.Line == 29);
        Assert.Contains(
            sequencePoints,
            point => point.Document.EndsWith("TaskRunner.cs", StringComparison.Ordinal)
                && point.Line == 8);
    }

    private static string SourceRevision(string informationalVersion)
    {
        var separator = informationalVersion.LastIndexOf('+');
        Assert.True(
            separator >= 0 && separator < informationalVersion.Length - 1,
            $"Informational version has no SourceRevisionId: {informationalVersion}");
        return informationalVersion[(separator + 1)..];
    }

    private static GitHeadCapability TryReadGitHead(string repositoryRoot)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git")
            {
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                ArgumentList = { "rev-parse", "HEAD" }
            });
            if (process is null)
            {
                return new(false, null, "git process could not be started");
            }

            if (!process.WaitForExit(milliseconds: 5_000))
            {
                process.Kill(entireProcessTree: true);
                return new(false, null, "git rev-parse timed out");
            }

            var head = process.StandardOutput.ReadToEnd().Trim();
            if (process.ExitCode != 0 || head.Length != 40)
            {
                return new(
                    false,
                    null,
                    $"git rev-parse unavailable (exit {process.ExitCode})");
            }

            return new(true, head, null);
        }
        catch (Exception ex)
        {
            return new(false, null, $"git unavailable: {ex.GetType().Name}");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Csdbg.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new Xunit.Sdk.XunitException(
            $"Could not locate Csdbg.slnx above {AppContext.BaseDirectory}.");
    }

    private sealed record GitHeadCapability(
        bool Available,
        string? Head,
        string? CapabilityReason);

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

    private static string ReplayAdapterPath =>
        Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "ReplayDapAdapter.exe" : "ReplayDapAdapter");

    private static List<int> EvidencePids(IEnumerable<JsonObject> evidence, string kind) =>
        evidence
            .Where(item => item["kind"]!.GetValue<string>() == kind)
            .Select(item => item["pid"]!.GetValue<int>())
            .ToList();

    private static async Task AssertProcessesExitedAsync(
        IEnumerable<int> processIds,
        TimeSpan timeout)
    {
        var remaining = processIds.Distinct().ToHashSet();
        var deadline = DateTime.UtcNow + timeout;
        while (remaining.Count > 0 && DateTime.UtcNow < deadline)
        {
            remaining.RemoveWhere(ProcessHasExited);
            if (remaining.Count > 0)
            {
                await Task.Delay(25);
            }
        }

        Assert.True(
            remaining.Count == 0,
            $"Replay processes still running: {string.Join(", ", remaining)}");
    }

    private static bool ProcessHasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private sealed class ReplayMcpProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _standardError;
        private bool _completed;

        public ReplayMcpProcess(string adapterPath, string evidenceDirectory)
        {
            var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
            var assemblyPath = typeof(McpServer).Assembly.Location;
            var startInfo = new ProcessStartInfo(dotnetHost)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(assemblyPath)!
            };
            startInfo.ArgumentList.Add(assemblyPath);
            startInfo.Environment["CSDBG_NETCOREDBG"] = adapterPath;
            startInfo.Environment["CSDBG_REPLAY_EVIDENCE_DIR"] = evidenceDirectory;
            _process = Process.Start(startInfo)
                ?? throw new Xunit.Sdk.XunitException("Failed to start csdbg MCP server.");
            _standardError = _process.StandardError.ReadToEndAsync();
        }

        public int Id => _process.Id;

        public async Task<string> SendAsync(string request, TimeSpan timeout)
        {
            await WriteAsync(request);
            return await ReadAsync(timeout);
        }

        public async Task WriteAsync(string request)
        {
            await _process.StandardInput.WriteLineAsync(request);
            await _process.StandardInput.FlushAsync();
        }

        public async Task<string> ReadAsync(TimeSpan timeout)
        {
            return await _process.StandardOutput.ReadLineAsync()
                    .WaitAsync(timeout)
                ?? throw new EndOfStreamException(
                    $"csdbg MCP server exited before responding. Evidence: {Environment.GetEnvironmentVariable("CSDBG_REPLAY_EVIDENCE_DIR")}");
        }

        public async Task<ReplayMcpResult> CompleteAsync(TimeSpan timeout)
        {
            _process.StandardInput.Close();
            await _process.WaitForExitAsync().WaitAsync(timeout);
            _completed = true;
            return new ReplayMcpResult(
                _process.ExitCode,
                await _process.StandardOutput.ReadToEndAsync(),
                await _standardError);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_completed && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TestTimeout);
            }

            _process.Dispose();
        }
    }

    private sealed record ReplayMcpResult(
        int ExitCode,
        string RemainingStandardOutput,
        string StandardError);

    private sealed class ReplayEnvironment : IDisposable
    {
        private const string Variable = "CSDBG_REPLAY_EVIDENCE_DIR";
        private readonly string? _previousValue = Environment.GetEnvironmentVariable(Variable);
        private bool _succeeded;

        public ReplayEnvironment()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                $"csdbg-replay-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DirectoryPath);
            Environment.SetEnvironmentVariable(Variable, DirectoryPath);
        }

        public string DirectoryPath { get; }

        public void MarkSucceeded() => _succeeded = true;

        public void ReleaseStop(int session) =>
            File.WriteAllText(
                Path.Combine(DirectoryPath, $"release-stop-{session}"),
                "release");

        public void RecordServerLifecycle(int processId, int exitCode) =>
            File.WriteAllText(
                Path.Combine(DirectoryPath, "server-lifecycle.json"),
                new JsonObject
                {
                    ["pid"] = processId,
                    ["exitCode"] = exitCode,
                    ["exited"] = true
                }.ToJsonString());

        public async Task<JsonObject[]> ReadEvidenceAsync(TimeSpan timeout)
            => await ReadEvidenceUntilAsync(_ => true, timeout);

        public async Task<JsonObject[]> ReadEvidenceUntilAsync(
            Func<JsonObject[], bool> condition,
            TimeSpan timeout)
        {
            var path = Path.Combine(DirectoryPath, "evidence.jsonl");
            var deadline = DateTime.UtcNow + timeout;
            JsonObject[] evidence = [];
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(path))
                {
                    evidence = (await File.ReadAllLinesAsync(path))
                        .Where(line => !string.IsNullOrWhiteSpace(line))
                        .Select(line => JsonNode.Parse(line)!.AsObject())
                        .ToArray();
                    if (condition(evidence))
                    {
                        return evidence;
                    }
                }

                await Task.Delay(25);
            }

            throw new Xunit.Sdk.XunitException(
                $"Replay evidence condition was not met within {timeout}. Last evidence: {string.Join(" | ", evidence.Select(item => item.ToJsonString()))}");
        }

        public async Task<JsonObject[]> ReadDiagnosticsAsync()
        {
            var path = Path.Combine(DirectoryPath, "diagnostics.jsonl");
            if (!File.Exists(path))
            {
                return [];
            }

            return (await File.ReadAllLinesAsync(path))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonNode.Parse(line)!.AsObject())
                .ToArray();
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(Variable, _previousValue);
            if (_succeeded)
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
