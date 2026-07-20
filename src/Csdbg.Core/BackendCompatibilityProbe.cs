using Csdbg.Core.Dap;

namespace Csdbg.Core;

public sealed record BackendProbeTarget(
    string Program,
    string? WorkingDirectory,
    string[] Arguments);

public sealed class BackendCompatibilityProbe(
    Func<BackendInfo> backendResolver,
    IDapClientFactory dapClientFactory)
{
    public async Task RunAsync(
        BackendProbeTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        await using var session = new DebugSession(backendResolver, dapClientFactory);
        try
        {
            await session.LaunchAsync(
                target.Program,
                target.WorkingDirectory,
                target.Arguments,
                stopAtEntry: true,
                cancellationToken);
            if (session.State != "stopped")
            {
                throw new InvalidOperationException(
                    $"Compatibility probe expected a managed stop, but debugger state is '{session.State}'.");
            }
        }
        finally
        {
            await session.StopAsync();
        }
    }
}
