using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Csdbg.Core.Tests;

public sealed class BackendArchiveExtractorTests
{
    private const long MaximumFileSize = 64L * 1024 * 1024;

    [Fact]
    public async Task ExtractAsync_ExtractsValidZipAndIgnoresMacOsMetadata()
    {
        using var archive = CreateZip(
            new("netcoredbg/", null),
            new("netcoredbg/bin/", null),
            new("netcoredbg/bin/netcoredbg", "debugger"),
            new("netcoredbg/LICENSE", "license"),
            new("__MACOSX/netcoredbg/._netcoredbg", "metadata"));
        using var destination = new TemporaryDirectory();

        await ExtractAsync(archive, BackendArchiveFormat.Zip, destination.Path);

        Assert.Equal("debugger", await File.ReadAllTextAsync(
            System.IO.Path.Combine(destination.Path, "netcoredbg", "bin", "netcoredbg")));
        Assert.Equal("license", await File.ReadAllTextAsync(
            System.IO.Path.Combine(destination.Path, "netcoredbg", "LICENSE")));
        Assert.False(Directory.Exists(System.IO.Path.Combine(destination.Path, "__MACOSX")));
        Assert.True(archive.CanRead);
    }

    [Fact]
    public async Task ExtractAsync_ExtractsValidTarGzipAndIgnoresMacOsMetadata()
    {
        using var archive = CreateTarGzip(
            new(TarEntryType.Directory, "netcoredbg/"),
            new(TarEntryType.Directory, "netcoredbg/bin/"),
            new(TarEntryType.RegularFile, "netcoredbg/bin/netcoredbg", "debugger"),
            new(TarEntryType.RegularFile, "netcoredbg/LICENSE", "license"),
            new(TarEntryType.RegularFile, "__MACOSX/netcoredbg/._netcoredbg", "metadata"));
        using var destination = new TemporaryDirectory();

        await ExtractAsync(archive, BackendArchiveFormat.TarGzip, destination.Path);

        Assert.Equal("debugger", await File.ReadAllTextAsync(
            System.IO.Path.Combine(destination.Path, "netcoredbg", "bin", "netcoredbg")));
        Assert.Equal("license", await File.ReadAllTextAsync(
            System.IO.Path.Combine(destination.Path, "netcoredbg", "LICENSE")));
        Assert.False(Directory.Exists(System.IO.Path.Combine(destination.Path, "__MACOSX")));
        Assert.True(archive.CanRead);
    }

    public static TheoryData<BackendArchiveFormat, string> UnsafePaths => new()
    {
        { BackendArchiveFormat.Zip, "netcoredbg/../escaped" },
        { BackendArchiveFormat.TarGzip, "netcoredbg/../escaped" },
        { BackendArchiveFormat.Zip, "/netcoredbg/absolute" },
        { BackendArchiveFormat.TarGzip, "/netcoredbg/absolute" },
        { BackendArchiveFormat.Zip, "C:/netcoredbg/absolute" },
        { BackendArchiveFormat.TarGzip, "C:/netcoredbg/absolute" },
        { BackendArchiveFormat.Zip, "netcoredbg\\..\\escaped" },
        { BackendArchiveFormat.TarGzip, "netcoredbg\\..\\escaped" },
        { BackendArchiveFormat.Zip, "netcoredbg//empty-segment" },
        { BackendArchiveFormat.TarGzip, "netcoredbg//empty-segment" }
    };

    [Theory]
    [MemberData(nameof(UnsafePaths))]
    public async Task ExtractAsync_RejectsUnsafePaths(
        BackendArchiveFormat format,
        string entryName)
    {
        using var archive = CreateArchive(format, new ArchiveItem(entryName, "bad"));
        using var destination = new TemporaryDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ExtractAsync(archive, format, destination.Path));

        Assert.False(File.Exists(System.IO.Path.Combine(destination.ParentPath, "escaped")));
    }

    [Theory]
    [InlineData(BackendArchiveFormat.Zip)]
    [InlineData(BackendArchiveFormat.TarGzip)]
    public async Task ExtractAsync_RejectsUnexpectedTopLevelRoots(BackendArchiveFormat format)
    {
        using var archive = CreateArchive(format, new ArchiveItem("other/file", "bad"));
        using var destination = new TemporaryDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ExtractAsync(archive, format, destination.Path));
    }

    [Theory]
    [InlineData(BackendArchiveFormat.Zip)]
    [InlineData(BackendArchiveFormat.TarGzip)]
    public async Task ExtractAsync_RejectsDuplicatePathsIgnoringCase(BackendArchiveFormat format)
    {
        using var archive = CreateArchive(
            format,
            new ArchiveItem("netcoredbg/tool", "one"),
            new ArchiveItem("netcoredbg/TOOL", "two"));
        using var destination = new TemporaryDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ExtractAsync(archive, format, destination.Path));
    }

    [Theory]
    [InlineData(BackendArchiveFormat.Zip)]
    [InlineData(BackendArchiveFormat.TarGzip)]
    public async Task ExtractAsync_RejectsFileDirectoryPathConflicts(BackendArchiveFormat format)
    {
        using var archive = CreateArchive(
            format,
            new ArchiveItem("netcoredbg/bin/tool", "child"),
            new ArchiveItem("netcoredbg/bin", "file"));
        using var destination = new TemporaryDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ExtractAsync(archive, format, destination.Path));
    }

    [Fact]
    public async Task ExtractAsync_RejectsZipSymlinks()
    {
        using var archive = CreateZip(
            new ZipItem("netcoredbg/link", "target", UnixExternalAttributes(0xa000)));
        using var destination = new TemporaryDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ExtractAsync(archive, BackendArchiveFormat.Zip, destination.Path));
    }

    [Fact]
    public async Task ExtractAsync_RejectsZipReparsePoints()
    {
        using var archive = CreateZip(
            new ZipItem("netcoredbg/link", "target", (int)FileAttributes.ReparsePoint));
        using var destination = new TemporaryDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ExtractAsync(archive, BackendArchiveFormat.Zip, destination.Path));
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink)]
    [InlineData(TarEntryType.HardLink)]
    public async Task ExtractAsync_RejectsTarLinks(TarEntryType entryType)
    {
        using var archive = CreateTarGzip(
            new TarItem(entryType, "netcoredbg/link", LinkName: "../target"));
        using var destination = new TemporaryDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ExtractAsync(archive, BackendArchiveFormat.TarGzip, destination.Path));
    }

    [Fact]
    public async Task ExtractAsync_RejectsDirectoryEntriesContainingData()
    {
        using var archive = CreateZip(new ZipItem("netcoredbg/not-a-directory/", "hidden"));
        using var destination = new TemporaryDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ExtractAsync(archive, BackendArchiveFormat.Zip, destination.Path));
    }

    [Fact]
    public async Task ExtractAsync_RejectsSpecialArchiveEntryTypes()
    {
        using var zip = CreateZip(
            new ZipItem("netcoredbg/socket", "bad", UnixExternalAttributes(0xc000)));
        using var tar = CreateTarGzip(new TarItem(TarEntryType.Fifo, "netcoredbg/pipe"));
        using var zipDestination = new TemporaryDirectory();
        using var tarDestination = new TemporaryDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ExtractAsync(zip, BackendArchiveFormat.Zip, zipDestination.Path));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => ExtractAsync(tar, BackendArchiveFormat.TarGzip, tarDestination.Path));
    }

    [Theory]
    [InlineData(BackendArchiveFormat.Zip)]
    [InlineData(BackendArchiveFormat.TarGzip)]
    public async Task ExtractAsync_RejectsMoreThan64RelevantEntries(BackendArchiveFormat format)
    {
        var items = Enumerable.Range(0, 65)
            .Select(index => new ArchiveItem($"netcoredbg/d{index}/", null))
            .ToArray();
        using var archive = CreateArchive(format, items);
        using var destination = new TemporaryDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ExtractAsync(archive, format, destination.Path));
    }

    [Theory]
    [InlineData(BackendArchiveFormat.Zip)]
    [InlineData(BackendArchiveFormat.TarGzip)]
    public async Task ExtractAsync_IgnoredMacOsMetadataCountsAgainstEntryLimit(
        BackendArchiveFormat format)
    {
        var items = Enumerable.Range(0, 65)
            .Select(index => new ArchiveItem($"__MACOSX/d{index}/", null))
            .ToArray();
        using var archive = CreateArchive(format, items);
        using var destination = new TemporaryDirectory();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => ExtractAsync(archive, format, destination.Path));

        Assert.Contains("more than 64 entries", exception.Message);
        Assert.False(Directory.Exists(System.IO.Path.Combine(destination.Path, "__MACOSX")));
    }

    [Fact]
    public async Task ExtractAsync_RejectsZipFilesOver64MiBFromMetadata()
    {
        using var archive = CreateZip(new ZipItem("netcoredbg/large", "x"));
        PatchCentralDirectorySizes(archive, checked((uint)(MaximumFileSize + 1)));
        using var destination = new TemporaryDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ExtractAsync(archive, BackendArchiveFormat.Zip, destination.Path));
    }

    [Fact]
    public async Task ExtractAsync_RejectsTarFilesOver64MiBFromMetadata()
    {
        using var archive = CreateTruncatedTarGzip("netcoredbg/large", MaximumFileSize + 1);
        using var destination = new TemporaryDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ExtractAsync(archive, BackendArchiveFormat.TarGzip, destination.Path));
    }

    [Theory]
    [InlineData(BackendArchiveFormat.Zip)]
    [InlineData(BackendArchiveFormat.TarGzip)]
    public async Task ExtractAsync_IgnoredMacOsMetadataCountsAgainstPerFileLimit(
        BackendArchiveFormat format)
    {
        using var archive = format switch
        {
            BackendArchiveFormat.Zip => CreateZipWithDeclaredSizes(
                new DeclaredArchiveFile("__MACOSX/netcoredbg/._large", MaximumFileSize + 1)),
            BackendArchiveFormat.TarGzip => CreateTruncatedTarGzip(
                "__MACOSX/netcoredbg/._large",
                MaximumFileSize + 1),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        using var destination = new TemporaryDirectory();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => ExtractAsync(archive, format, destination.Path));

        Assert.Contains($"{MaximumFileSize} byte file limit", exception.Message);
        Assert.False(Directory.Exists(System.IO.Path.Combine(destination.Path, "__MACOSX")));
    }

    [Fact]
    public async Task ExtractAsync_RejectsTotalExpandedContentOver128MiB()
    {
        using var archive = CreateZip(
            new("netcoredbg/a", "a"),
            new("netcoredbg/b", "b"),
            new("netcoredbg/c", "c"));
        PatchCentralDirectorySizes(archive, 44_739_243, 44_739_243, 44_739_243);
        using var destination = new TemporaryDirectory();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => ExtractAsync(archive, BackendArchiveFormat.Zip, destination.Path));
        Assert.False(Directory.Exists(System.IO.Path.Combine(destination.Path, "netcoredbg")));
    }

    [Theory]
    [InlineData(BackendArchiveFormat.Zip)]
    [InlineData(BackendArchiveFormat.TarGzip)]
    public async Task ExtractAsync_IgnoredMacOsMetadataCountsAgainstTotalExpandedLimit(
        BackendArchiveFormat format)
    {
        DeclaredArchiveFile[] files =
        [
            new("__MACOSX/netcoredbg/._a", 44_739_243),
            new("__MACOSX/netcoredbg/._b", 44_739_243),
            new("__MACOSX/netcoredbg/._c", 44_739_243)
        ];
        using var archive = format switch
        {
            BackendArchiveFormat.Zip => CreateZipWithDeclaredSizes(files),
            BackendArchiveFormat.TarGzip => CreateTarGzipWithDeclaredFiles(files),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        using var destination = new TemporaryDirectory();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => ExtractAsync(archive, format, destination.Path));

        Assert.Contains("134217728 byte expanded-content limit", exception.Message);
        Assert.False(Directory.Exists(System.IO.Path.Combine(destination.Path, "__MACOSX")));
    }

    [Fact]
    public async Task ExtractAsync_RejectsPreExistingDestinationSymlinks()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var archive = CreateZip(new ZipItem("netcoredbg/bin/tool", "bad"));
        using var destination = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        Directory.CreateDirectory(System.IO.Path.Combine(destination.Path, "netcoredbg"));
        Directory.CreateSymbolicLink(
            System.IO.Path.Combine(destination.Path, "netcoredbg", "bin"),
            outside.Path);

        await Assert.ThrowsAsync<IOException>(
            () => ExtractAsync(archive, BackendArchiveFormat.Zip, destination.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
    }

    [Fact]
    public async Task ExtractAsync_HonorsPreCanceledCancellationTokens()
    {
        using var archive = CreateZip(new ZipItem("netcoredbg/tool", "content"));
        using var destination = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new SafeBackendArchiveExtractor().ExtractAsync(
                archive,
                BackendArchiveFormat.Zip,
                destination.Path,
                cancellation.Token));
    }

    private static Task ExtractAsync(
        Stream archive,
        BackendArchiveFormat format,
        string destinationPath) =>
        new SafeBackendArchiveExtractor().ExtractAsync(archive, format, destinationPath);

    private static MemoryStream CreateArchive(
        BackendArchiveFormat format,
        params ArchiveItem[] items) => format switch
    {
        BackendArchiveFormat.Zip => CreateZip(items.Select(item =>
            new ZipItem(item.Name, item.Content)).ToArray()),
        BackendArchiveFormat.TarGzip => CreateTarGzip(items.Select(item =>
            new TarItem(
                item.Content is null ? TarEntryType.Directory : TarEntryType.RegularFile,
                item.Name,
                item.Content)).ToArray()),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private static MemoryStream CreateZip(params ZipItem[] items)
    {
        var result = new MemoryStream();
        using (var archive = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var item in items)
            {
                var entry = archive.CreateEntry(item.Name, CompressionLevel.Fastest);
                entry.ExternalAttributes = item.ExternalAttributes;
                if (item.Content is not null)
                {
                    using var writer = new StreamWriter(
                        entry.Open(),
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                        leaveOpen: false);
                    writer.Write(item.Content);
                }
            }
        }

        result.Position = 0;
        return result;
    }

    private static MemoryStream CreateZipWithDeclaredSizes(params DeclaredArchiveFile[] files)
    {
        var archive = CreateZip(files.Select(file => new ZipItem(file.Name, "x")).ToArray());
        PatchCentralDirectorySizes(
            archive,
            files.Select(file => checked((uint)file.Length)).ToArray());
        return archive;
    }

    private static MemoryStream CreateTarGzip(params TarItem[] items)
    {
        var result = new MemoryStream();
        using (var gzip = new GZipStream(result, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            foreach (var item in items)
            {
                var entry = new PaxTarEntry(item.Type, item.Name);
                if (item.Content is not null)
                {
                    entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(item.Content));
                }

                if (item.LinkName is not null)
                {
                    entry.LinkName = item.LinkName;
                }

                writer.WriteEntry(entry);
            }
        }

        result.Position = 0;
        return result;
    }

    private static MemoryStream CreateTarGzipWithDeclaredFiles(
        params DeclaredArchiveFile[] files)
    {
        var result = new MemoryStream();
        using (var gzip = new GZipStream(result, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            foreach (var file in files)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, file.Name)
                {
                    DataStream = new GeneratedZeroStream(file.Length)
                };
                writer.WriteEntry(entry);
            }
        }

        result.Position = 0;
        return result;
    }

    private static MemoryStream CreateTruncatedTarGzip(string name, long declaredSize)
    {
        var header = new byte[512];
        Encoding.ASCII.GetBytes(name).CopyTo(header, 0);
        WriteTarOctal(header.AsSpan(100, 8), 0x1a4);
        WriteTarOctal(header.AsSpan(108, 8), 0);
        WriteTarOctal(header.AsSpan(116, 8), 0);
        WriteTarOctal(header.AsSpan(124, 12), declaredSize);
        WriteTarOctal(header.AsSpan(136, 12), 0);
        header.AsSpan(148, 8).Fill((byte)' ');
        header[156] = (byte)'0';
        Encoding.ASCII.GetBytes("ustar\0").CopyTo(header, 257);
        Encoding.ASCII.GetBytes("00").CopyTo(header, 263);
        WriteTarOctal(header.AsSpan(148, 8), header.Sum(value => (long)value));

        var result = new MemoryStream();
        using (var gzip = new GZipStream(result, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(header);
        }

        result.Position = 0;
        return result;
    }

    private static void WriteTarOctal(Span<byte> destination, long value)
    {
        destination.Fill((byte)'0');
        var text = Convert.ToString(value, 8)!;
        Encoding.ASCII.GetBytes(text).CopyTo(destination[(destination.Length - text.Length - 1)..]);
        destination[^1] = 0;
    }

    private static void PatchCentralDirectorySizes(MemoryStream archive, params uint[] sizes)
    {
        var bytes = archive.GetBuffer().AsSpan(0, checked((int)archive.Length));
        var patched = 0;
        for (var index = 0; index <= bytes.Length - 46 && patched < sizes.Length; index++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[index..]) != 0x02014b50)
            {
                continue;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(bytes[(index + 24)..], sizes[patched++]);
        }

        Assert.Equal(sizes.Length, patched);
        archive.Position = 0;
    }

    private static int UnixExternalAttributes(int type) => unchecked((type | 0x1ff) << 16);

    private sealed record ArchiveItem(string Name, string? Content);

    private sealed record DeclaredArchiveFile(string Name, long Length);

    private sealed record ZipItem(string Name, string? Content, int ExternalAttributes = 0);

    private sealed record TarItem(
        TarEntryType Type,
        string Name,
        string? Content = null,
        string? LinkName = null);

    private sealed class GeneratedZeroStream(long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = (int)Math.Min(count, length - _position);
            buffer.AsSpan(offset, bytesRead).Clear();
            _position += bytesRead;
            return bytesRead;
        }

        public override int Read(Span<byte> buffer)
        {
            var bytesRead = (int)Math.Min(buffer.Length, length - _position);
            buffer[..bytesRead].Clear();
            _position += bytesRead;
            return bytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            return _position;
        }

        public override void Flush() => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            ParentPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "csdbg-archive-tests",
                Guid.NewGuid().ToString("N"));
            Path = System.IO.Path.Combine(ParentPath, "destination");
            Directory.CreateDirectory(Path);
        }

        public string ParentPath { get; }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(ParentPath))
            {
                Directory.Delete(ParentPath, recursive: true);
            }
        }
    }
}
