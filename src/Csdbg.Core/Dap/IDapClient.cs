using System.Text.Json.Nodes;

namespace Csdbg.Core.Dap;

public interface IDapClient : IAsyncDisposable
{
    bool IsRunning { get; }
    event Action<JsonObject>? EventReceived;
    event Action<Exception>? Closed;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task<JsonObject> SendRequestAsync(
        string command,
        JsonObject? arguments = null,
        CancellationToken cancellationToken = default);
}

public interface IDapClientFactory
{
    IDapClient Create(string netcoredbgPath);
}

public sealed class DapClientFactory : IDapClientFactory
{
    public IDapClient Create(string netcoredbgPath) => new DapClient(netcoredbgPath);
}
