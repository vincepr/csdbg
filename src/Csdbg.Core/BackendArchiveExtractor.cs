using System.Formats.Tar;
using System.IO.Compression;

namespace Csdbg.Core;

public interface IBackendArchiveExtractor
{
    Task ExtractAsync(
        Stream archive,
        BackendArchiveFormat format,
        string destinationDirectory,
        CancellationToken cancellationToken = default);
}

public sealed class SafeBackendArchiveExtractor : IBackendArchiveExtractor
{
    private const int MaximumRelevantEntries = 64;
    private const long MaximumFileSize = 64L * 1024 * 1024;
    private const long MaximumExpandedSize = 128L * 1024 * 1024;
    private const int UnixFileTypeMask = 0xf000;
    private const int UnixRegularFile = 0x8000;
    private const int UnixDirectory = 0x4000;

    public async Task ExtractAsync(
        Stream archive,
        BackendArchiveFormat format,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        if (!archive.CanRead)
        {
            throw new ArgumentException("The archive stream must be readable.", nameof(archive));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        EnsureSafeDirectory(destinationRoot);

        switch (format)
        {
            case BackendArchiveFormat.Zip:
                await ExtractZipAsync(archive, destinationRoot, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case BackendArchiveFormat.TarGzip:
                await ExtractTarGzipAsync(archive, destinationRoot, cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported archive format.");
        }
    }

    private static async Task ExtractZipAsync(
        Stream archive,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        var state = new ValidationState();
        var entries = new List<ValidatedZipEntry>();

        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kind = GetZipEntryKind(entry);
            var path = ValidateEntry(entry.FullName, kind, entry.Length, state);
            if (path is not null)
            {
                entries.Add(new ValidatedZipEntry(entry, path, kind));
            }
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationPath = GetDestinationPath(destinationRoot, entry.Path);
            if (entry.Kind == ArchiveEntryKind.Directory)
            {
                EnsureSafeDirectory(destinationPath);
                continue;
            }

            EnsureSafeDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var source = entry.Entry.Open();
            await CopyFileAsync(source, destinationPath, entry.Entry.Length, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task ExtractTarGzipAsync(
        Stream archive,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new TarReader(gzip, leaveOpen: true);
        var state = new ValidationState();

        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false)
            is { } entry)
        {
            var kind = GetTarEntryKind(entry);
            var path = ValidateEntry(entry.Name, kind, entry.Length, state);
            if (path is null)
            {
                continue;
            }

            var destinationPath = GetDestinationPath(destinationRoot, path);
            if (kind == ArchiveEntryKind.Directory)
            {
                EnsureSafeDirectory(destinationPath);
                continue;
            }

            EnsureSafeDirectory(Path.GetDirectoryName(destinationPath)!);
            var source = entry.DataStream ?? Stream.Null;
            await CopyFileAsync(source, destinationPath, entry.Length, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static ArchiveEntryKind GetZipEntryKind(ZipArchiveEntry entry)
    {
        var unixType = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        if (unixType is not 0 and not UnixRegularFile and not UnixDirectory)
        {
            throw InvalidArchive($"Archive entry '{entry.FullName}' is not a regular file or directory.");
        }

        if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
        {
            throw InvalidArchive($"Archive entry '{entry.FullName}' is a reparse point.");
        }

        var nameIsDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
        var attributesIndicateDirectory =
            unixType == UnixDirectory
            || (entry.ExternalAttributes & (int)FileAttributes.Directory) != 0;
        if (nameIsDirectory != attributesIndicateDirectory && attributesIndicateDirectory)
        {
            throw InvalidArchive($"Archive entry '{entry.FullName}' has inconsistent directory metadata.");
        }

        if (nameIsDirectory && unixType == UnixRegularFile)
        {
            throw InvalidArchive($"Archive entry '{entry.FullName}' has inconsistent file metadata.");
        }

        return nameIsDirectory ? ArchiveEntryKind.Directory : ArchiveEntryKind.File;
    }

    private static ArchiveEntryKind GetTarEntryKind(TarEntry entry) => entry.EntryType switch
    {
        TarEntryType.Directory => ArchiveEntryKind.Directory,
        TarEntryType.RegularFile or TarEntryType.V7RegularFile => ArchiveEntryKind.File,
        _ => throw InvalidArchive(
            $"Archive entry '{entry.Name}' has unsupported type '{entry.EntryType}'.")
    };

    private static string? ValidateEntry(
        string entryName,
        ArchiveEntryKind kind,
        long length,
        ValidationState state)
    {
        var segments = ValidatePortablePath(entryName, kind);
        if (kind == ArchiveEntryKind.Directory && length != 0)
        {
            throw InvalidArchive($"Archive directory '{entryName}' contains file data.");
        }

        if (segments[0].Equals("__MACOSX", StringComparison.Ordinal))
        {
            return null;
        }

        if (!segments[0].Equals("netcoredbg", StringComparison.Ordinal))
        {
            throw InvalidArchive($"Archive entry '{entryName}' is outside the 'netcoredbg/' root.");
        }

        if (segments.Length == 1 && kind != ArchiveEntryKind.Directory)
        {
            throw InvalidArchive("The 'netcoredbg' archive root must be a directory.");
        }

        if (++state.RelevantEntryCount > MaximumRelevantEntries)
        {
            throw InvalidArchive($"Archive contains more than {MaximumRelevantEntries} relevant entries.");
        }

        if (kind == ArchiveEntryKind.File)
        {
            if (length < 0 || length > MaximumFileSize)
            {
                throw InvalidArchive($"Archive entry '{entryName}' exceeds the {MaximumFileSize} byte file limit.");
            }

            state.ExpandedSize += length;
            if (state.ExpandedSize > MaximumExpandedSize)
            {
                throw InvalidArchive($"Archive exceeds the {MaximumExpandedSize} byte expanded-content limit.");
            }
        }

        var path = string.Join(Path.DirectorySeparatorChar, segments);
        state.AddPath(path, kind, entryName);
        return path;
    }

    private static string[] ValidatePortablePath(string entryName, ArchiveEntryKind kind)
    {
        if (string.IsNullOrEmpty(entryName)
            || entryName[0] == '/'
            || entryName.Contains('\\', StringComparison.Ordinal)
            || entryName.Contains('\0', StringComparison.Ordinal))
        {
            throw InvalidArchive($"Archive entry has an unsafe path: '{entryName}'.");
        }

        var path = kind == ArchiveEntryKind.Directory && entryName.EndsWith("/", StringComparison.Ordinal)
            ? entryName[..^1]
            : entryName;
        var segments = path.Split('/');
        if (segments.Length == 0
            || segments.Any(segment =>
                segment.Length == 0
                || segment is "." or ".."
                || segment.Contains(':', StringComparison.Ordinal)))
        {
            throw InvalidArchive($"Archive entry has an unsafe path: '{entryName}'.");
        }

        return segments;
    }

    private static string GetDestinationPath(string destinationRoot, string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(destinationRoot, relativePath));
        var rootPrefix = destinationRoot.EndsWith(Path.DirectorySeparatorChar)
            ? destinationRoot
            : destinationRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, PathComparison))
        {
            throw InvalidArchive($"Archive path '{relativePath}' escapes the destination directory.");
        }

        return path;
    }

    private static void EnsureSafeDirectory(string path)
    {
        if (PathExists(path))
        {
            RejectLinkOrReparsePoint(path);
            if (!Directory.Exists(path))
            {
                throw new IOException($"Extraction directory path '{path}' is not a directory.");
            }

            return;
        }

        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent) && parent != path)
        {
            EnsureSafeDirectory(parent);
        }

        Directory.CreateDirectory(path);
        RejectLinkOrReparsePoint(path);
    }

    private static async Task CopyFileAsync(
        Stream source,
        string destinationPath,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        RejectExistingPath(destinationPath);
        var created = false;
        try
        {
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            created = true;
            var buffer = new byte[81920];
            long copied = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                copied += read;
                if (copied > expectedLength || copied > MaximumFileSize)
                {
                    throw InvalidArchive($"Archive entry '{destinationPath}' expanded beyond its declared size.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (copied != expectedLength)
            {
                throw InvalidArchive($"Archive entry '{destinationPath}' did not match its declared size.");
            }
        }
        catch
        {
            if (created)
            {
                File.Delete(destinationPath);
            }

            throw;
        }
    }

    private static void RejectExistingPath(string path)
    {
        if (PathExists(path))
        {
            RejectLinkOrReparsePoint(path);
            throw new IOException($"Extraction destination '{path}' already exists.");
        }
    }

    private static bool PathExists(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            return true;
        }

        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void RejectLinkOrReparsePoint(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"Extraction path '{path}' is a link or reparse point.");
        }
    }

    private static InvalidDataException InvalidArchive(string message) => new(message);

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private enum ArchiveEntryKind
    {
        File,
        Directory
    }

    private sealed record ValidatedZipEntry(
        ZipArchiveEntry Entry,
        string Path,
        ArchiveEntryKind Kind);

    private sealed class ValidationState
    {
        private readonly Dictionary<string, ArchiveEntryKind> paths =
            new(StringComparer.OrdinalIgnoreCase);

        public int RelevantEntryCount { get; set; }

        public long ExpandedSize { get; set; }

        public void AddPath(string path, ArchiveEntryKind kind, string entryName)
        {
            if (!paths.TryAdd(path, kind))
            {
                throw InvalidArchive($"Archive contains duplicate path '{entryName}'.");
            }

            var parent = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(parent))
            {
                if (paths.TryGetValue(parent, out var parentKind)
                    && parentKind == ArchiveEntryKind.File)
                {
                    throw InvalidArchive($"Archive file path '{parent}' is also used as a directory.");
                }

                parent = Path.GetDirectoryName(parent);
            }

            if (kind == ArchiveEntryKind.File)
            {
                var prefix = path + Path.DirectorySeparatorChar;
                if (paths.Keys.Any(existing => existing.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    throw InvalidArchive($"Archive file path '{entryName}' is also used as a directory.");
                }
            }
        }
    }
}
