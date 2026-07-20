using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Csdbg.Core;

public sealed record CommandProbeResult(int ExitCode, string StandardOutput, string StandardError);

public interface ICommandProbe
{
    Task<CommandProbeResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

public sealed class ProcessCommandProbe : ICommandProbe
{
    public async Task<CommandProbeResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start command: {fileName}");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        return new CommandProbeResult(
            process.ExitCode,
            await outputTask,
            await errorTask);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between cancellation and cleanup.
        }
    }
}

public sealed record BackendHealthResult(
    bool Healthy,
    string OperatingSystem,
    string Architecture,
    BackendInfo Backend,
    string? BackendVersion,
    bool? DebuggerCompatible,
    string[] DotnetRuntimes,
    string[] Errors);

public sealed class BackendHealthChecker(
    Func<BackendInfo> backendResolver,
    ICommandProbe commandProbe,
    Func<CancellationToken, Task>? compatibilityProbe = null)
{
    public async Task<BackendHealthResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var backend = backendResolver();
        var errors = new List<string>();
        string? backendVersion = null;
        bool? debuggerCompatible = null;
        string[] runtimes = [];

        if (!backend.Available || backend.Path is null)
        {
            errors.Add(backend.Error ?? "netcoredbg is unavailable.");
        }
        else
        {
            try
            {
                var version = await commandProbe.RunAsync(backend.Path, ["--version"], cancellationToken);
                backendVersion = FirstNonEmpty(version.StandardOutput, version.StandardError);
                if (version.ExitCode != 0)
                {
                    errors.Add($"netcoredbg --version exited with code {version.ExitCode}.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"netcoredbg probe failed: {ex.Message}");
            }
        }

        try
        {
            var runtimeProbe = await commandProbe.RunAsync("dotnet", ["--list-runtimes"], cancellationToken);
            runtimes = runtimeProbe.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.StartsWith("Microsoft.NETCore.App ", StringComparison.Ordinal))
                .ToArray();
            if (runtimeProbe.ExitCode != 0)
            {
                errors.Add($"dotnet --list-runtimes exited with code {runtimeProbe.ExitCode}.");
            }
            else if (runtimes.Length == 0)
            {
                errors.Add("No Microsoft.NETCore.App runtime was detected.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add($"dotnet runtime probe failed: {ex.Message}");
        }

        if (compatibilityProbe is not null && errors.Count == 0)
        {
            try
            {
                await compatibilityProbe(cancellationToken);
                debuggerCompatible = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                debuggerCompatible = false;
                errors.Add($"debugger compatibility probe failed: {ex.Message}");
            }
        }

        return new BackendHealthResult(
            errors.Count == 0,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            backend,
            backendVersion,
            debuggerCompatible,
            runtimes,
            errors.ToArray());
    }

    private static string? FirstNonEmpty(params string[] values) =>
        values
            .SelectMany(value => value.Split('\n', StringSplitOptions.TrimEntries))
            .FirstOrDefault(value => value.Length > 0);
}
