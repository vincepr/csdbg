using System.Diagnostics;

namespace Csdbg.Core.Dap;

public interface IDapProcess : IDisposable
{
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    TextReader StandardError { get; }
    bool HasExited { get; }

    void Kill();
    Task WaitForExitAsync(CancellationToken cancellationToken = default);
}

public interface IDapProcessFactory
{
    IDapProcess Start(string executablePath);
}

public sealed class DapProcessFactory : IDapProcessFactory
{
    public IDapProcess Start(string executablePath)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--interpreter=vscode");

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start netcoredbg.");
        return new SystemDapProcess(process);
    }

    private sealed class SystemDapProcess(Process process) : IDapProcess
    {
        public Stream StandardInput => process.StandardInput.BaseStream;
        public Stream StandardOutput => process.StandardOutput.BaseStream;
        public TextReader StandardError => process.StandardError;
        public bool HasExited => process.HasExited;

        public void Kill() => process.Kill(entireProcessTree: true);

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
            process.WaitForExitAsync(cancellationToken);

        public void Dispose() => process.Dispose();
    }
}
