using System.Reflection;
using System.Text.Json;
using Csdbg.Core;
using Csdbg.Core.Dap;

if (args is ["--compatibility-probe"])
{
    return 0;
}

if (args is ["--help"])
{
    Console.WriteLine(Usage());
    return 0;
}

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
    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new { installed = false, error = "netcoredbg installation timed out." },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return 1;
    }
}

if (args is ["--check"])
{
    using var timeout = new CancellationTokenSource(GetCheckTimeout());
    var compatibilityProbe = new BackendCompatibilityProbe(
        BackendLocator.FindNetcoredbg,
        new DapClientFactory());
    var probeTarget = CreateCompatibilityProbeTarget();
    var checker = new BackendHealthChecker(
        BackendLocator.FindNetcoredbg,
        new ProcessCommandProbe(),
        cancellationToken => compatibilityProbe.RunAsync(probeTarget, cancellationToken));
    try
    {
        var result = await checker.CheckAsync(timeout.Token);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return result.Healthy ? 0 : 1;
    }
    catch (OperationCanceledException) when (timeout.IsCancellationRequested)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                healthy = false,
                debuggerCompatible = false,
                errors = new[] { "Debugger health check timed out." }
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return 1;
    }
}

if (args.Length != 0)
{
    Console.Error.WriteLine($"Unknown arguments: {string.Join(' ', args)}");
    Console.Error.WriteLine(Usage());
    return 2;
}

await using var session = new DebugSession();
var server = new McpServer(session, Console.In, Console.Out);
await server.RunAsync();
return 0;

static BackendProbeTarget CreateCompatibilityProbeTarget()
{
    var processPath = Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot determine the current process path.");
    var entryAssembly = Assembly.GetEntryAssembly()?.Location;
    var isDotnetHost = Path.GetFileNameWithoutExtension(processPath)
        .Equals("dotnet", StringComparison.OrdinalIgnoreCase);

    if (isDotnetHost)
    {
        if (string.IsNullOrWhiteSpace(entryAssembly))
        {
            throw new InvalidOperationException("Cannot determine the MCP entry assembly path.");
        }

        return new BackendProbeTarget(
            processPath,
            Path.GetDirectoryName(entryAssembly),
            [entryAssembly, "--compatibility-probe"]);
    }

    return new BackendProbeTarget(
        processPath,
        Path.GetDirectoryName(processPath),
        ["--compatibility-probe"]);
}

static TimeSpan GetCheckTimeout()
{
    var value = Environment.GetEnvironmentVariable("CSDBG_CHECK_TIMEOUT_MS");
    return int.TryParse(value, out var milliseconds) && milliseconds > 0
        ? TimeSpan.FromMilliseconds(milliseconds)
        : TimeSpan.FromSeconds(10);
}

static string Usage() =>
    "Usage: csdbg-mcp [--check | --install-netcoredbg | --help]";
