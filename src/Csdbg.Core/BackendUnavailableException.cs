namespace Csdbg.Core;

public sealed class BackendUnavailableException(string? message)
    : InvalidOperationException(message ?? "Debugger backend is unavailable.");
