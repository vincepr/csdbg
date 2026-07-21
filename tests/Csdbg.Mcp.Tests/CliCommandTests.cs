using System.Diagnostics;
using System.Text.Json;

namespace Csdbg.Mcp.Tests;

public sealed class CliCommandTests
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);
    private const string Usage = "Usage: csdbg [--check | --install-netcoredbg | --version | --help]";
    private const string StartupMessage = "csdbg MCP server running on stdio; waiting for client input.";

    [Theory(Timeout = 15_000)]
    [InlineData("--unknown")]
    [InlineData("--check", "extra")]
    public async Task InvalidArgumentsPrintUsageAndExitTwo(params string[] arguments)
    {
        var result = await RunCliAsync(arguments);

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains(Usage, result.StandardError, StringComparison.Ordinal);
    }

    [Fact(Timeout = 15_000)]
    public async Task HelpPrintsUsageAndExitsZero()
    {
        var result = await RunCliAsync(["--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(Usage, result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("dotnet tool install --global Csdbg.Mcp", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("csdbg --install-netcoredbg", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"command\": \"csdbg\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Empty(result.StandardError);
    }

    [Fact(Timeout = 15_000)]
    public async Task VersionPrintsPackageVersionAndExitsZero()
    {
        var result = await RunCliAsync(["--version"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("csdbg 0.2.1", result.StandardOutput.Trim());
        Assert.Empty(result.StandardError);
    }

    [Fact(Timeout = 15_000)]
    public async Task BareCommandLogsStdioStartupToStandardErrorAndExitsOnEof()
    {
        var result = await RunCliAsync([], closeStandardInput: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(StartupMessage, result.StandardError.Trim());
    }

    [Fact(Timeout = 15_000)]
    public async Task SigtermPerformsGracefulServerShutdown()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = StartCli([]);
        var startup = await process.StandardError.ReadLineAsync().WaitAsync(ProcessTimeout);
        Assert.Equal(StartupMessage, startup);

        using var signal = Process.Start(new ProcessStartInfo("kill", ["-TERM", process.Id.ToString()])
        {
            UseShellExecute = false
        }) ?? throw new Xunit.Sdk.XunitException("Failed to send SIGTERM to csdbg.");
        await signal.WaitForExitAsync().WaitAsync(ProcessTimeout);
        Assert.Equal(0, signal.ExitCode);

        await process.WaitForExitAsync().WaitAsync(ProcessTimeout);
        Assert.Equal(0, process.ExitCode);
        Assert.Empty(await process.StandardOutput.ReadToEndAsync());
    }

    [Fact(Timeout = 15_000)]
    public async Task CheckTimeoutReturnsSingleUnhealthyJsonDocument()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"csdbg-cli-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var fakeDebugger = Path.Combine(tempDirectory, "netcoredbg");
        try
        {
            await File.WriteAllTextAsync(fakeDebugger, "#!/bin/sh\nsleep 30\n");
            File.SetUnixFileMode(
                fakeDebugger,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var result = await RunCliAsync(
                ["--check"],
                new Dictionary<string, string>
                {
                    ["CSDBG_NETCOREDBG"] = fakeDebugger,
                    ["CSDBG_CHECK_TIMEOUT_MS"] = "100"
                });

            Assert.Equal(1, result.ExitCode);
            Assert.Empty(result.StandardError);
            var outputLines = result.StandardOutput.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var output = Assert.Single(outputLines);
            using var document = JsonDocument.Parse(output);
            Assert.False(document.RootElement.GetProperty("healthy").GetBoolean());
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static async Task<CommandResult> RunCliAsync(
        string[] arguments,
        IReadOnlyDictionary<string, string>? environment = null,
        bool closeStandardInput = false)
    {
        using var process = StartCli(arguments, environment);
        if (closeStandardInput)
        {
            process.StandardInput.Close();
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(ProcessTimeout);
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }

        return new CommandResult(process.ExitCode, await outputTask, await errorTask);
    }

    private static Process StartCli(
        string[] arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var assemblyPath = typeof(McpServer).Assembly.Location;
        var startInfo = new ProcessStartInfo(dotnetHost)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)!
        };
        startInfo.ArgumentList.Add(assemblyPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        return Process.Start(startInfo)
            ?? throw new Xunit.Sdk.XunitException("Failed to start csdbg.");
    }

    private sealed record CommandResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
