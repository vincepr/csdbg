namespace Csdbg.Core;

public sealed class BackendInfo
{
    public string Name { get; init; } = "netcoredbg";
    public string? Path { get; init; }
    public bool Available => Path is not null;
    public string? Error { get; init; }
}
