using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Csdbg.Core.Tests;

public sealed class BackendHealthCheckerTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(3);
    private const string BackendPath = "/fake/netcoredbg";
    private const string RuntimeLine = "Microsoft.NETCore.App 10.0.9 [/fake/dotnet/shared/Microsoft.NETCore.App]";

    [Fact]
    public async Task CheckAsync_HealthyBackendAndRuntime_ReturnsHealthyDetails()
    {
        var backend = new BackendInfo { Path = BackendPath };
        var probe = new ScriptedCommandProbe();
        probe.EnqueueResult(
            BackendPath,
            ["--version"],
            new(0, "\n  \r\nNET Core debugger 3.1.0\nCopyright (c) Example\n", ""));
        probe.EnqueueResult(
            "dotnet",
            ["--list-runtimes"],
            new(0, $"Microsoft.AspNetCore.App 10.0.9 [/fake/aspnet]\n{RuntimeLine}\n", ""));
        var checker = new BackendHealthChecker(() => backend, probe);

        var result = await checker.CheckAsync();

        Assert.True(result.Healthy);
        Assert.Same(backend, result.Backend);
        Assert.Equal("NET Core debugger 3.1.0", result.BackendVersion);
        Assert.Equal([RuntimeLine], result.DotnetRuntimes);
        Assert.Empty(result.Errors);
        Assert.Equal(RuntimeInformation.OSDescription, result.OperatingSystem);
        Assert.Equal(RuntimeInformation.ProcessArchitecture.ToString(), result.Architecture);
        Assert.Equal(2, probe.CallCount);
    }

    [Fact]
    public async Task CheckAsync_CompatibilityProbeSucceeds_ReportsCompatible()
    {
        var probe = CreateProbeWithBackendResult(new(0, "netcoredbg 3.1.0", ""));
        probe.EnqueueResult("dotnet", ["--list-runtimes"], new(0, RuntimeLine, ""));
        using var cancellation = new CancellationTokenSource();
        var compatibilityCallCount = 0;
        var checker = new BackendHealthChecker(
            () => new BackendInfo { Path = BackendPath },
            probe,
            token =>
            {
                compatibilityCallCount++;
                Assert.Equal(cancellation.Token, token);
                return Task.CompletedTask;
            });

        var result = await checker.CheckAsync(cancellation.Token);

        Assert.True(result.Healthy);
        Assert.True(result.DebuggerCompatible);
        Assert.Empty(result.Errors);
        Assert.Equal(1, compatibilityCallCount);
        Assert.Equal(2, probe.CallCount);
    }

    [Fact]
    public async Task CheckAsync_CompatibilityProbeFails_ReturnsUnhealthyWithError()
    {
        var probe = CreateProbeWithBackendResult(new(0, "netcoredbg 3.1.0", ""));
        probe.EnqueueResult("dotnet", ["--list-runtimes"], new(0, RuntimeLine, ""));
        var checker = new BackendHealthChecker(
            () => new BackendInfo { Path = BackendPath },
            probe,
            _ => Task.FromException(new InvalidOperationException("managed launch was rejected")));

        var result = await checker.CheckAsync();

        Assert.False(result.Healthy);
        Assert.False(result.DebuggerCompatible);
        Assert.Equal(
            ["debugger compatibility probe failed: managed launch was rejected"],
            result.Errors);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckAsync_BackendOrRuntimePrerequisiteFails_SkipsCompatibilityProbe(
        bool backendFails)
    {
        var backend = backendFails
            ? new BackendInfo { Error = "netcoredbg was not found" }
            : new BackendInfo { Path = BackendPath };
        var runtimeResult = backendFails
            ? new CommandProbeResult(0, RuntimeLine, "")
            : new CommandProbeResult(23, RuntimeLine, "runtime probe failed");
        var probe = new ScriptedCommandProbe();
        if (!backendFails)
        {
            probe.EnqueueResult(
                BackendPath,
                ["--version"],
                new CommandProbeResult(0, "netcoredbg 3.1.0", ""));
        }

        probe.EnqueueResult("dotnet", ["--list-runtimes"], runtimeResult);
        var compatibilityCallCount = 0;
        var checker = new BackendHealthChecker(
            () => backend,
            probe,
            _ =>
            {
                compatibilityCallCount++;
                return Task.CompletedTask;
            });

        var result = await checker.CheckAsync();

        Assert.False(result.Healthy);
        Assert.Null(result.DebuggerCompatible);
        Assert.Equal(0, compatibilityCallCount);
        Assert.Single(result.Errors);
        Assert.Equal(backendFails ? 1 : 2, probe.CallCount);
    }

    [Fact]
    public async Task CheckAsync_CancellationFromCompatibilityProbe_IsPropagated()
    {
        var probe = CreateProbeWithBackendResult(new(0, "netcoredbg 3.1.0", ""));
        probe.EnqueueResult("dotnet", ["--list-runtimes"], new(0, RuntimeLine, ""));
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checker = new BackendHealthChecker(
            () => new BackendInfo { Path = BackendPath },
            probe,
            async token =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });

        var checkTask = checker.CheckAsync(cancellation.Token);
        await started.Task.WaitAsync(TestTimeout);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => checkTask.WaitAsync(TestTimeout));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(2, probe.CallCount);
    }

    [Fact]
    public async Task CheckAsync_UnavailableBackend_SkipsBackendProbe()
    {
        var backend = new BackendInfo { Error = "netcoredbg was not found" };
        var probe = new ScriptedCommandProbe();
        probe.EnqueueResult("dotnet", ["--list-runtimes"], new(0, RuntimeLine, ""));
        var checker = new BackendHealthChecker(() => backend, probe);

        var result = await checker.CheckAsync();

        Assert.False(result.Healthy);
        Assert.Same(backend, result.Backend);
        Assert.Null(result.BackendVersion);
        Assert.Equal(["netcoredbg was not found"], result.Errors);
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public async Task CheckAsync_BackendProbeReturnsNonzero_ReportsExitCodeAndVersion()
    {
        var probe = CreateProbeWithBackendResult(new(17, "", "netcoredbg 3.1.0\n"));
        probe.EnqueueResult("dotnet", ["--list-runtimes"], new(0, RuntimeLine, ""));
        var checker = CreateChecker(probe);

        var result = await checker.CheckAsync();

        Assert.False(result.Healthy);
        Assert.Equal("netcoredbg 3.1.0", result.BackendVersion);
        Assert.Equal(["netcoredbg --version exited with code 17."], result.Errors);
    }

    [Fact]
    public async Task CheckAsync_NoNetCoreRuntime_ReportsMissingRuntime()
    {
        var probe = CreateProbeWithBackendResult(new(0, "netcoredbg 3.1.0", ""));
        probe.EnqueueResult(
            "dotnet",
            ["--list-runtimes"],
            new(0, "Microsoft.AspNetCore.App 10.0.9 [/fake/aspnet]", ""));
        var checker = CreateChecker(probe);

        var result = await checker.CheckAsync();

        Assert.False(result.Healthy);
        Assert.Empty(result.DotnetRuntimes);
        Assert.Equal(["No Microsoft.NETCore.App runtime was detected."], result.Errors);
    }

    [Fact]
    public async Task CheckAsync_RuntimeProbeReturnsNonzero_ReportsExitCodeEvenWhenRuntimeIsListed()
    {
        var probe = CreateProbeWithBackendResult(new(0, "netcoredbg 3.1.0", ""));
        probe.EnqueueResult("dotnet", ["--list-runtimes"], new(23, RuntimeLine, "failure"));
        var checker = CreateChecker(probe);

        var result = await checker.CheckAsync();

        Assert.False(result.Healthy);
        Assert.Equal([RuntimeLine], result.DotnetRuntimes);
        Assert.Equal(["dotnet --list-runtimes exited with code 23."], result.Errors);
        Assert.DoesNotContain("No Microsoft.NETCore.App runtime was detected.", result.Errors);
    }

    [Fact]
    public async Task CheckAsync_BackendProbeThrows_ReportsFailureAndContinuesRuntimeProbe()
    {
        var probe = new ScriptedCommandProbe();
        probe.EnqueueException(BackendPath, ["--version"], new InvalidOperationException("probe exploded"));
        probe.EnqueueResult("dotnet", ["--list-runtimes"], new(0, RuntimeLine, ""));
        var checker = CreateChecker(probe);

        var result = await checker.CheckAsync();

        Assert.False(result.Healthy);
        Assert.Null(result.BackendVersion);
        Assert.Equal([RuntimeLine], result.DotnetRuntimes);
        Assert.Equal(["netcoredbg probe failed: probe exploded"], result.Errors);
        Assert.Equal(2, probe.CallCount);
    }

    [Fact]
    public async Task CheckAsync_CancellationFromProbe_IsPropagated()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new ScriptedCommandProbe();
        probe.Enqueue(
            BackendPath,
            ["--version"],
            async token =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new CommandProbeResult(0, "unreachable", "");
            });
        var checker = CreateChecker(probe);

        var checkTask = checker.CheckAsync(cancellation.Token);
        await started.Task.WaitAsync(TestTimeout);
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => checkTask.WaitAsync(TestTimeout));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public async Task RunAsync_Cancellation_TerminatesLongRunningProcessOnPosix()
    {
        // A POSIX shell provides a portable way to publish the process ID before exec'ing sleep.
        if (OperatingSystem.IsWindows() || !File.Exists("/bin/sh"))
        {
            return;
        }

        var pidFile = Path.Combine(Path.GetTempPath(), $"csdbg-probe-{Guid.NewGuid():N}.pid");
        using var cancellation = new CancellationTokenSource();
        var probe = new ProcessCommandProbe();
        var runTask = probe.RunAsync(
            "/bin/sh",
            ["-c", $"echo $$ > '{pidFile}'; exec sleep 30"],
            cancellation.Token);
        int? processId = null;

        try
        {
            await WaitUntilAsync(() => File.Exists(pidFile), TestTimeout);
            processId = int.Parse(
                (await File.ReadAllTextAsync(pidFile)).Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture);

            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => runTask.WaitAsync(TestTimeout));
            await WaitUntilAsync(() => !IsProcessRunning(processId.Value), TestTimeout);

            Assert.False(IsProcessRunning(processId.Value));
        }
        finally
        {
            cancellation.Cancel();
            if (processId is int pid)
            {
                await TerminateProcessAsync(pid);
            }

            File.Delete(pidFile);
        }
    }

    private static BackendHealthChecker CreateChecker(ICommandProbe probe) =>
        new(() => new BackendInfo { Path = BackendPath }, probe);

    private static ScriptedCommandProbe CreateProbeWithBackendResult(CommandProbeResult result)
    {
        var probe = new ScriptedCommandProbe();
        probe.EnqueueResult(BackendPath, ["--version"], result);
        return probe;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException($"Condition was not met within {timeout}.");
            }

            await Task.Delay(20);
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task TerminateProcessAsync(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TestTimeout);
        }
        catch (ArgumentException)
        {
        }
    }

    private sealed class ScriptedCommandProbe : ICommandProbe
    {
        private readonly Queue<ExpectedCall> _expectedCalls = [];

        public int CallCount { get; private set; }

        public void EnqueueResult(
            string fileName,
            string[] arguments,
            CommandProbeResult result) =>
            Enqueue(fileName, arguments, _ => Task.FromResult(result));

        public void EnqueueException(
            string fileName,
            string[] arguments,
            Exception exception) =>
            Enqueue(fileName, arguments, _ => Task.FromException<CommandProbeResult>(exception));

        public void Enqueue(
            string fileName,
            string[] arguments,
            Func<CancellationToken, Task<CommandProbeResult>> handler) =>
            _expectedCalls.Enqueue(new ExpectedCall(fileName, arguments, handler));

        public Task<CommandProbeResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var expected = _expectedCalls.Dequeue();
            Assert.Equal(expected.FileName, fileName);
            Assert.Equal(expected.Arguments, arguments);
            return expected.Handler(cancellationToken);
        }

        private sealed record ExpectedCall(
            string FileName,
            string[] Arguments,
            Func<CancellationToken, Task<CommandProbeResult>> Handler);
    }
}
