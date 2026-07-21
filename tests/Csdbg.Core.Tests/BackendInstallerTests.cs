using System.Net;
using System.Security.Cryptography;

namespace Csdbg.Core.Tests;

public sealed class BackendInstallerTests
{
    private const long MaxDownloadBytes = 32L * 1024 * 1024;

    [Fact]
    public async Task InstallAsync_DownloadsExactAssetAndInstallsVerifiedBackend()
    {
        using var temp = new TempDirectory();
        var archiveBytes = "verified archive"u8.ToArray();
        var asset = CreateAsset(archiveBytes);
        var handler = new RecordingHttpMessageHandler(
            (_, _) => Task.FromResult(CreateResponse(HttpStatusCode.OK, archiveBytes)));
        using var httpClient = new HttpClient(handler);
        var extractor = new RecordingArchiveExtractor(
            (_, _, destination, _) =>
            {
                CreateStagedExecutable(destination, asset, executable: false);
                return Task.CompletedTask;
            });
        var probe = SuccessfulProbe(asset);
        var installer = new BackendInstaller(httpClient, extractor, probe);

        var result = await installer.InstallAsync(asset, temp.Root);

        var expectedPath = BackendInstallPaths.GetExecutablePath(temp.Root, asset);
        Assert.True(result.Installed);
        Assert.False(result.AlreadyInstalled);
        Assert.Equal(expectedPath, result.Path);
        Assert.Equal(asset.Tag, result.Tag);
        Assert.Equal(asset.Commit, result.Commit);
        Assert.True(File.Exists(expectedPath));
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal(
            "https://github.com/Samsung/netcoredbg/releases/download/test-tag/netcoredbg-test.tar.gz",
            handler.RequestUri?.AbsoluteUri);
        var extraction = Assert.Single(extractor.Calls);
        Assert.Equal(archiveBytes, extraction.ArchiveBytes);
        Assert.Equal(asset.ArchiveFormat, extraction.Format);
        Assert.Equal(1, probe.CallCount);
        Assert.Equal(["--buildinfo"], probe.Arguments);
        Assert.NotNull(probe.FileName);
        Assert.Contains(".staging", probe.FileName, StringComparison.Ordinal);
        Assert.EndsWith(
            Path.Combine("netcoredbg", asset.ExecutableName),
            probe.FileName,
            StringComparison.Ordinal);
        AssertNoTemporaryResidue(temp.Root);
    }

    [Fact]
    public async Task InstallAsync_DeletesDownloadWhenShaDoesNotMatch()
    {
        using var temp = new TempDirectory();
        var downloadedBytes = "tampered archive"u8.ToArray();
        var asset = CreateAsset("expected archive"u8.ToArray());
        using var httpClient = CreateHttpClient(HttpStatusCode.OK, downloadedBytes);
        var extractor = new RecordingArchiveExtractor();
        var probe = new RecordingCommandProbe();
        var installer = new BackendInstaller(httpClient, extractor, probe);

        var exception = await Assert.ThrowsAsync<BackendInstallException>(
            () => installer.InstallAsync(asset, temp.Root));

        Assert.Contains("SHA-256 mismatch", exception.Message);
        Assert.Empty(extractor.Calls);
        Assert.Equal(0, probe.CallCount);
        AssertFailedInstallWasCleaned(temp.Root, asset);
    }

    [Fact]
    public async Task InstallAsync_ReportsHttpFailureAndLeavesNoResidue()
    {
        using var temp = new TempDirectory();
        var asset = CreateAsset([]);
        using var httpClient = CreateHttpClient(HttpStatusCode.BadGateway, []);
        var extractor = new RecordingArchiveExtractor();
        var installer = new BackendInstaller(httpClient, extractor, new RecordingCommandProbe());

        var exception = await Assert.ThrowsAsync<BackendInstallException>(
            () => installer.InstallAsync(asset, temp.Root));

        Assert.Contains("HTTP 502", exception.Message);
        Assert.Empty(extractor.Calls);
        AssertFailedInstallWasCleaned(temp.Root, asset);
    }

    [Fact]
    public async Task InstallAsync_RejectsDeclaredContentLengthOverLimitAndLeavesNoResidue()
    {
        using var temp = new TempDirectory();
        var asset = CreateAsset([]);
        var content = new ByteArrayContent([]);
        content.Headers.ContentLength = MaxDownloadBytes + 1;
        using var httpClient = CreateHttpClient(HttpStatusCode.OK, content);
        var extractor = new RecordingArchiveExtractor();
        var installer = new BackendInstaller(httpClient, extractor, new RecordingCommandProbe());

        var exception = await Assert.ThrowsAsync<BackendInstallException>(
            () => installer.InstallAsync(asset, temp.Root));

        Assert.Contains($"{MaxDownloadBytes} byte limit", exception.Message);
        Assert.Empty(extractor.Calls);
        AssertFailedInstallWasCleaned(temp.Root, asset);
    }

    [Fact]
    public async Task InstallAsync_RejectsStreamedContentOverLimitAndLeavesNoResidue()
    {
        using var temp = new TempDirectory();
        var asset = CreateAsset([]);
        var content = new StreamContent(new GeneratedStream(MaxDownloadBytes + 1));
        Assert.Null(content.Headers.ContentLength);
        using var httpClient = CreateHttpClient(HttpStatusCode.OK, content);
        var extractor = new RecordingArchiveExtractor();
        var installer = new BackendInstaller(httpClient, extractor, new RecordingCommandProbe());

        var exception = await Assert.ThrowsAsync<BackendInstallException>(
            () => installer.InstallAsync(asset, temp.Root));

        Assert.Contains($"{MaxDownloadBytes} byte limit", exception.Message);
        Assert.Empty(extractor.Calls);
        AssertFailedInstallWasCleaned(temp.Root, asset);
    }

    [Fact]
    public async Task InstallAsync_RejectsArchiveWithoutExecutableAndCleansStaging()
    {
        using var temp = new TempDirectory();
        var archiveBytes = "archive without executable"u8.ToArray();
        var asset = CreateAsset(archiveBytes);
        using var httpClient = CreateHttpClient(HttpStatusCode.OK, archiveBytes);
        var extractor = new RecordingArchiveExtractor(
            (_, _, destination, _) =>
            {
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "unrelated.txt"), "not netcoredbg");
                return Task.CompletedTask;
            });
        var probe = new RecordingCommandProbe();
        var installer = new BackendInstaller(httpClient, extractor, probe);

        var exception = await Assert.ThrowsAsync<BackendInstallException>(
            () => installer.InstallAsync(asset, temp.Root));

        Assert.Contains("did not contain netcoredbg", exception.Message);
        Assert.Single(extractor.Calls);
        Assert.Equal(0, probe.CallCount);
        AssertFailedInstallWasCleaned(temp.Root, asset);
    }

    [Theory]
    [InlineData(VerificationFailure.NonzeroExit)]
    [InlineData(VerificationFailure.WrongCommit)]
    [InlineData(VerificationFailure.ProbeException)]
    public async Task InstallAsync_RejectsFailedBuildInfoAndCleansStaging(
        VerificationFailure failure)
    {
        using var temp = new TempDirectory();
        var archiveBytes = "backend archive"u8.ToArray();
        var asset = CreateAsset(archiveBytes);
        using var httpClient = CreateHttpClient(HttpStatusCode.OK, archiveBytes);
        var extractor = ExtractorThatCreatesBackend(asset);
        var probe = new RecordingCommandProbe((_, _, _) => failure switch
        {
            VerificationFailure.NonzeroExit => Task.FromResult(
                new CommandProbeResult(23, "", "failed")),
            VerificationFailure.WrongCommit => Task.FromResult(
                new CommandProbeResult(0, "commit: 0000000", "")),
            VerificationFailure.ProbeException => Task.FromException<CommandProbeResult>(
                new InvalidOperationException("cannot execute")),
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        });
        var installer = new BackendInstaller(httpClient, extractor, probe);

        var exception = await Assert.ThrowsAsync<BackendInstallException>(
            () => installer.InstallAsync(asset, temp.Root));

        var expectedMessage = failure switch
        {
            VerificationFailure.NonzeroExit => "exited with code 23",
            VerificationFailure.WrongCommit => $"expected commit {asset.Commit[..7]}",
            VerificationFailure.ProbeException => "verification failed: cannot execute",
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };
        Assert.Contains(expectedMessage, exception.Message);
        if (failure is VerificationFailure.ProbeException)
        {
            Assert.IsType<InvalidOperationException>(exception.InnerException);
        }

        Assert.Single(extractor.Calls);
        Assert.Equal(1, probe.CallCount);
        AssertFailedInstallWasCleaned(temp.Root, asset);
    }

    [Fact]
    public async Task InstallAsync_PropagatesCancellationAndCleansDownloadedAndStagedFiles()
    {
        using var temp = new TempDirectory();
        using var cancellation = new CancellationTokenSource();
        var archiveBytes = "backend archive"u8.ToArray();
        var asset = CreateAsset(archiveBytes);
        using var httpClient = CreateHttpClient(HttpStatusCode.OK, archiveBytes);
        var extractor = ExtractorThatCreatesBackend(asset);
        var probe = new RecordingCommandProbe((_, _, cancellationToken) =>
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation was not observed.");
        });
        var installer = new BackendInstaller(httpClient, extractor, probe);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => installer.InstallAsync(asset, temp.Root, cancellation.Token));

        Assert.Single(extractor.Calls);
        Assert.Equal(1, probe.CallCount);
        AssertFailedInstallWasCleaned(temp.Root, asset);
    }

    [Fact]
    public async Task InstallAsync_WhenAlreadyInstalled_VerifiesWithoutDownloadOrExtraction()
    {
        using var temp = new TempDirectory();
        var asset = CreateAsset([]);
        var executablePath = BackendInstallPaths.GetExecutablePath(temp.Root, asset);
        CreateFile(executablePath, executable: true);
        var handler = new RecordingHttpMessageHandler(
            (_, _) => throw new InvalidOperationException("HTTP must not be called."));
        using var httpClient = new HttpClient(handler);
        var extractor = new RecordingArchiveExtractor();
        var probe = SuccessfulProbe(asset);
        var installer = new BackendInstaller(httpClient, extractor, probe);

        var result = await installer.InstallAsync(asset, temp.Root);

        Assert.False(result.Installed);
        Assert.True(result.AlreadyInstalled);
        Assert.Equal(executablePath, result.Path);
        Assert.Equal(0, handler.CallCount);
        Assert.Empty(extractor.Calls);
        Assert.Equal(1, probe.CallCount);
        Assert.Equal(executablePath, probe.FileName);
        AssertNoTemporaryResidue(temp.Root);
    }

    [Fact]
    public async Task InstallAsync_WhenManagedExecutableHasWrongCommit_ReplacesItWithPinnedBackend()
    {
        using var temp = new TempDirectory();
        var archiveBytes = "replacement backend archive"u8.ToArray();
        var asset = CreateAsset(archiveBytes);
        var executablePath = BackendInstallPaths.GetExecutablePath(temp.Root, asset);
        var versionDirectory = BackendInstallPaths.GetVersionDirectory(temp.Root, asset);
        CreateFile(executablePath, executable: true, content: "corrupt backend");
        File.WriteAllText(Path.Combine(versionDirectory, "obsolete.txt"), "old version residue");
        var handler = new RecordingHttpMessageHandler(
            (_, _) => Task.FromResult(CreateResponse(HttpStatusCode.OK, archiveBytes)));
        using var httpClient = new HttpClient(handler);
        var extractor = new RecordingArchiveExtractor(
            (_, _, destination, _) =>
            {
                CreateStagedExecutable(
                    destination,
                    asset,
                    executable: false,
                    content: "pinned backend");
                return Task.CompletedTask;
            });
        var probe = new RecordingCommandProbe((fileName, _, _) =>
        {
            if (fileName == executablePath)
            {
                return Task.FromResult(new CommandProbeResult(0, "commit: 0000000", ""));
            }

            Assert.Contains(".staging", fileName, StringComparison.Ordinal);
            Assert.Equal("pinned backend", File.ReadAllText(fileName));
            return Task.FromResult(
                new CommandProbeResult(0, $"commit: {asset.Commit[..7]}", ""));
        });
        var installer = new BackendInstaller(httpClient, extractor, probe);

        var result = await installer.InstallAsync(asset, temp.Root);

        Assert.True(result.Installed);
        Assert.False(result.AlreadyInstalled);
        Assert.Equal(executablePath, result.Path);
        Assert.Equal("pinned backend", File.ReadAllText(executablePath));
        Assert.False(File.Exists(Path.Combine(versionDirectory, "obsolete.txt")));
        Assert.Equal(1, handler.CallCount);
        Assert.Single(extractor.Calls);
        Assert.Equal(2, probe.CallCount);
        AssertNoTemporaryResidue(temp.Root);
    }

    [Fact]
    public async Task InstallAsync_WhenVersionDirectoryIsIncomplete_ReplacesItWithPinnedBackend()
    {
        using var temp = new TempDirectory();
        var archiveBytes = "recovery backend archive"u8.ToArray();
        var asset = CreateAsset(archiveBytes);
        var versionDirectory = BackendInstallPaths.GetVersionDirectory(temp.Root, asset);
        Directory.CreateDirectory(versionDirectory);
        File.WriteAllText(Path.Combine(versionDirectory, "partial.txt"), "incomplete install");
        using var httpClient = CreateHttpClient(HttpStatusCode.OK, archiveBytes);
        var extractor = ExtractorThatCreatesBackend(asset, content: "recovered backend");
        var probe = SuccessfulProbe(asset);
        var installer = new BackendInstaller(httpClient, extractor, probe);

        var result = await installer.InstallAsync(asset, temp.Root);

        Assert.True(result.Installed);
        Assert.False(result.AlreadyInstalled);
        Assert.Equal("recovered backend", File.ReadAllText(result.Path));
        Assert.False(File.Exists(Path.Combine(versionDirectory, "partial.txt")));
        Assert.Single(extractor.Calls);
        Assert.Equal(1, probe.CallCount);
        AssertNoTemporaryResidue(temp.Root);
    }

    [Fact]
    public async Task InstallAsync_WhenReplacementPublishFails_RestoresOldVersionAndCleansResidue()
    {
        using var temp = new TempDirectory();
        var archiveBytes = "failed replacement archive"u8.ToArray();
        var asset = CreateAsset(archiveBytes);
        var executablePath = BackendInstallPaths.GetExecutablePath(temp.Root, asset);
        var versionDirectory = BackendInstallPaths.GetVersionDirectory(temp.Root, asset);
        CreateFile(executablePath, executable: true, content: "old backend");
        File.WriteAllText(Path.Combine(versionDirectory, "old-marker.txt"), "preserve me");
        using var httpClient = CreateHttpClient(HttpStatusCode.OK, archiveBytes);
        var extractor = ExtractorThatCreatesBackend(asset, content: "replacement backend");
        var probe = new RecordingCommandProbe((fileName, _, _) =>
        {
            if (fileName == executablePath)
            {
                return Task.FromResult(new CommandProbeResult(0, "commit: 0000000", ""));
            }

            var stagingDirectory = Directory.GetParent(
                Directory.GetParent(fileName)!.FullName)!.FullName;
            Directory.Delete(stagingDirectory, recursive: true);
            return Task.FromResult(
                new CommandProbeResult(0, $"commit: {asset.Commit[..7]}", ""));
        });
        var installer = new BackendInstaller(httpClient, extractor, probe);

        await Assert.ThrowsAnyAsync<IOException>(
            () => installer.InstallAsync(asset, temp.Root));

        Assert.Equal("old backend", File.ReadAllText(executablePath));
        Assert.Equal(
            "preserve me",
            File.ReadAllText(Path.Combine(versionDirectory, "old-marker.txt")));
        Assert.Single(extractor.Calls);
        Assert.Equal(2, probe.CallCount);
        AssertNoTemporaryResidue(temp.Root);
    }

    [Fact]
    public async Task InstallAsync_OnUnix_SetsExpectedExecutableModeBeforeVerification()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        var archiveBytes = "unix backend archive"u8.ToArray();
        var asset = CreateAsset(archiveBytes);
        using var httpClient = CreateHttpClient(HttpStatusCode.OK, archiveBytes);
        var extractor = ExtractorThatCreatesBackend(asset, executable: false);
        const UnixFileMode expectedMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        var probe = SuccessfulProbe(asset);
        var installer = new BackendInstaller(httpClient, extractor, probe);

        var result = await installer.InstallAsync(asset, temp.Root);

        Assert.Equal(expectedMode, File.GetUnixFileMode(result.Path));
        AssertNoTemporaryResidue(temp.Root);
    }

    private static NetcoredbgReleaseAsset CreateAsset(byte[] expectedArchive) =>
        new(
            "test-tag",
            "abcdef0123456789abcdef0123456789abcdef01",
            "netcoredbg-test.tar.gz",
            Convert.ToHexString(SHA256.HashData(expectedArchive)).ToLowerInvariant(),
            BackendArchiveFormat.TarGzip,
            "netcoredbg");

    private static HttpClient CreateHttpClient(HttpStatusCode statusCode, byte[] content) =>
        CreateHttpClient(statusCode, new ByteArrayContent(content));

    private static HttpClient CreateHttpClient(HttpStatusCode statusCode, HttpContent content) =>
        new(new RecordingHttpMessageHandler(
            (_, _) => Task.FromResult(CreateResponse(statusCode, content))));

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, byte[] content) =>
        CreateResponse(statusCode, new ByteArrayContent(content));

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, HttpContent content) =>
        new(statusCode) { Content = content };

    private static RecordingArchiveExtractor ExtractorThatCreatesBackend(
        NetcoredbgReleaseAsset asset,
        bool executable = false,
        string content = "fake netcoredbg") =>
        new((_, _, destination, _) =>
        {
            CreateStagedExecutable(destination, asset, executable, content);
            return Task.CompletedTask;
        });

    private static RecordingCommandProbe SuccessfulProbe(NetcoredbgReleaseAsset asset) =>
        new((_, arguments, _) =>
        {
            Assert.Equal(["--buildinfo"], arguments);
            return Task.FromResult(
                new CommandProbeResult(0, "", $"commit: {asset.Commit[..7]}")
            );
        });

    private static string CreateStagedExecutable(
        string destination,
        NetcoredbgReleaseAsset asset,
        bool executable,
        string content = "fake netcoredbg")
    {
        var path = Path.Combine(destination, "netcoredbg", asset.ExecutableName);
        CreateFile(path, executable, content);
        return path;
    }

    private static void CreateFile(
        string path,
        bool executable,
        string content = "fake netcoredbg")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (executable)
            {
                mode |= UnixFileMode.UserExecute;
            }

            File.SetUnixFileMode(path, mode);
        }
    }

    private static void AssertFailedInstallWasCleaned(
        string installRoot,
        NetcoredbgReleaseAsset asset)
    {
        Assert.False(Directory.Exists(BackendInstallPaths.GetVersionDirectory(installRoot, asset)));
        AssertNoTemporaryResidue(installRoot);
    }

    private static void AssertNoTemporaryResidue(string installRoot)
    {
        var backendRoot = Path.Combine(installRoot, "netcoredbg");
        if (!Directory.Exists(backendRoot))
        {
            return;
        }

        var residue = Directory.EnumerateFileSystemEntries(backendRoot)
            .Where(path =>
                Path.GetFileName(path).EndsWith(".download", StringComparison.Ordinal)
                || Path.GetFileName(path).EndsWith(".staging", StringComparison.Ordinal)
                || Path.GetFileName(path).EndsWith(".replaced", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(residue);
    }

    public enum VerificationFailure
    {
        NonzeroExit,
        WrongCommit,
        ProbeException
    }

    private sealed class RecordingHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            return handler(request, cancellationToken);
        }
    }

    private sealed class RecordingArchiveExtractor(
        Func<Stream, BackendArchiveFormat, string, CancellationToken, Task>? handler = null)
        : IBackendArchiveExtractor
    {
        public List<ExtractionCall> Calls { get; } = [];

        public async Task ExtractAsync(
            Stream archive,
            BackendArchiveFormat format,
            string destinationDirectory,
            CancellationToken cancellationToken = default)
        {
            using var captured = new MemoryStream();
            await archive.CopyToAsync(captured, cancellationToken);
            Calls.Add(new ExtractionCall(captured.ToArray(), format, destinationDirectory));
            if (handler is not null)
            {
                await handler(archive, format, destinationDirectory, cancellationToken);
            }
        }
    }

    private sealed record ExtractionCall(
        byte[] ArchiveBytes,
        BackendArchiveFormat Format,
        string DestinationDirectory);

    private sealed class RecordingCommandProbe(
        Func<string, IReadOnlyList<string>, CancellationToken, Task<CommandProbeResult>>? handler = null)
        : ICommandProbe
    {
        public int CallCount { get; private set; }

        public string? FileName { get; private set; }

        public IReadOnlyList<string>? Arguments { get; private set; }

        public Task<CommandProbeResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            FileName = fileName;
            Arguments = arguments.ToArray();
            return handler?.Invoke(fileName, arguments, cancellationToken)
                ?? throw new InvalidOperationException("Unexpected command probe call.");
        }
    }

    private sealed class GeneratedStream : Stream
    {
        private readonly long _length;
        private long _remaining;

        public GeneratedStream(long length)
        {
            _length = length;
            _remaining = length;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _length - _remaining;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = (int)Math.Min(count, _remaining);
            buffer.AsSpan(offset, bytesRead).Clear();
            _remaining -= bytesRead;
            return bytesRead;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesRead = (int)Math.Min(buffer.Length, _remaining);
            buffer.Span[..bytesRead].Clear();
            _remaining -= bytesRead;
            return ValueTask.FromResult(bytesRead);
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), $"csdbg-installer-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
