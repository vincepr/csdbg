using System.Security.Cryptography;

namespace Csdbg.Core;

public sealed record BackendInstallResult(
    bool Installed,
    bool AlreadyInstalled,
    string Path,
    string Tag,
    string Commit);

public sealed class BackendInstallException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public sealed class BackendInstaller(
    HttpClient httpClient,
    IBackendArchiveExtractor archiveExtractor,
    ICommandProbe commandProbe)
{
    private const long MaxDownloadBytes = 32L * 1024 * 1024;

    public async Task<BackendInstallResult> InstallAsync(
        NetcoredbgReleaseAsset asset,
        string installRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        var versionDirectory = BackendInstallPaths.GetVersionDirectory(installRoot, asset);
        var executablePath = BackendInstallPaths.GetExecutablePath(installRoot, asset);
        if (File.Exists(executablePath))
        {
            try
            {
                await VerifyBackendAsync(executablePath, asset, cancellationToken);
                return CreateResult(installed: false, alreadyInstalled: true, executablePath, asset);
            }
            catch (BackendInstallException)
            {
                // A pinned managed install is repairable from its verified release asset.
            }
        }

        var backendRoot = Path.GetDirectoryName(versionDirectory)
            ?? throw new BackendInstallException("Cannot determine the backend installation directory.");
        Directory.CreateDirectory(backendRoot);

        var operationId = Guid.NewGuid().ToString("N");
        var archivePath = Path.Combine(backendRoot, $".{asset.Tag}-{operationId}.download");
        var stagingDirectory = Path.Combine(backendRoot, $".{asset.Tag}-{operationId}.staging");

        try
        {
            await DownloadAsync(asset, archivePath, cancellationToken);
            await using (var archive = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await archiveExtractor.ExtractAsync(
                    archive,
                    asset.ArchiveFormat,
                    stagingDirectory,
                    cancellationToken);
            }

            var stagedExecutable = Path.Combine(
                stagingDirectory,
                "netcoredbg",
                asset.ExecutableName);
            if (!File.Exists(stagedExecutable))
            {
                throw new BackendInstallException(
                    $"The {asset.FileName} archive did not contain {asset.ExecutableName}.");
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    stagedExecutable,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            await VerifyBackendAsync(stagedExecutable, asset, cancellationToken);

            await PublishAsync(
                stagingDirectory,
                versionDirectory,
                executablePath,
                asset,
                cancellationToken);

            return CreateResult(installed: true, alreadyInstalled: false, executablePath, asset);
        }
        finally
        {
            DeleteFileIfPresent(archivePath);
            DeleteDirectoryIfPresent(stagingDirectory);
        }
    }

    private async Task PublishAsync(
        string stagingDirectory,
        string versionDirectory,
        string executablePath,
        NetcoredbgReleaseAsset asset,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(versionDirectory))
        {
            try
            {
                Directory.Move(stagingDirectory, versionDirectory);
                return;
            }
            catch (IOException) when (File.Exists(executablePath))
            {
                await VerifyBackendAsync(executablePath, asset, cancellationToken);
                return;
            }
        }

        var backupDirectory = $"{versionDirectory}.{Guid.NewGuid():N}.replaced";
        Directory.Move(versionDirectory, backupDirectory);
        try
        {
            Directory.Move(stagingDirectory, versionDirectory);
            DeleteDirectoryIfPresent(backupDirectory);
        }
        catch
        {
            if (!Directory.Exists(versionDirectory) && Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, versionDirectory);
            }

            throw;
        }
    }

    private async Task DownloadAsync(
        NetcoredbgReleaseAsset asset,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            asset.DownloadUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new BackendInstallException(
                $"Downloading {asset.FileName} failed with HTTP {(int)response.StatusCode}.");
        }

        if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
        {
            throw new BackendInstallException(
                $"The {asset.FileName} download exceeds the {MaxDownloadBytes} byte limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[81920];
        long totalBytes = 0;
        int bytesRead;
        while ((bytesRead = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            totalBytes += bytesRead;
            if (totalBytes > MaxDownloadBytes)
            {
                throw new BackendInstallException(
                    $"The {asset.FileName} download exceeds the {MaxDownloadBytes} byte limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }

        await destination.FlushAsync(cancellationToken);
        destination.Close();

        await using var downloadedFile = File.OpenRead(destinationPath);
        var actualHash = Convert.ToHexString(
            await SHA256.HashDataAsync(downloadedFile, cancellationToken))
            .ToLowerInvariant();
        if (!actualHash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new BackendInstallException(
                $"SHA-256 mismatch for {asset.FileName}: expected {asset.Sha256}, got {actualHash}.");
        }
    }

    private async Task VerifyBackendAsync(
        string executablePath,
        NetcoredbgReleaseAsset asset,
        CancellationToken cancellationToken)
    {
        CommandProbeResult probe;
        try
        {
            probe = await commandProbe.RunAsync(executablePath, ["--buildinfo"], cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BackendInstallException(
                $"Installed netcoredbg verification failed: {ex.Message}",
                ex);
        }

        if (probe.ExitCode != 0)
        {
            throw new BackendInstallException(
                $"Installed netcoredbg --buildinfo exited with code {probe.ExitCode}.");
        }

        var output = $"{probe.StandardOutput}\n{probe.StandardError}";
        var expectedCommit = asset.Commit[..7];
        if (!output.Contains(expectedCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new BackendInstallException(
                $"Installed netcoredbg does not report expected commit {expectedCommit}.");
        }
    }

    private static BackendInstallResult CreateResult(
        bool installed,
        bool alreadyInstalled,
        string executablePath,
        NetcoredbgReleaseAsset asset) =>
        new(installed, alreadyInstalled, executablePath, asset.Tag, asset.Commit);

    private static void DeleteFileIfPresent(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static void DeleteDirectoryIfPresent(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
