using System.Text;

namespace Csdbg.Core.Tests;

public sealed class SourceContextReaderTests
{
    [Fact]
    public void TryRead_AtStart_ReturnsAvailableForwardWindow()
    {
        using var source = SourceFile.Create(NumberedLines(10));

        var context = SourceContextReader.TryRead(source.Path, 1);

        AssertContext(context, 1, 4, 1, [1, 2, 3, 4]);
    }

    [Fact]
    public void TryRead_InMiddle_ReturnsThreeLinesOnEachSide()
    {
        using var source = SourceFile.Create(NumberedLines(10));

        var context = SourceContextReader.TryRead(source.Path, 5);

        AssertContext(context, 2, 8, 5, [2, 3, 4, 5, 6, 7, 8]);
    }

    [Fact]
    public void TryRead_AtEnd_ReturnsAvailableBackwardWindow()
    {
        using var source = SourceFile.Create(NumberedLines(10));

        var context = SourceContextReader.TryRead(source.Path, 10);

        AssertContext(context, 7, 10, 10, [7, 8, 9, 10]);
    }

    [Fact]
    public void TryRead_PreservesIndentationAndMarksExactlyTheRequestedLine()
    {
        using var source = SourceFile.Create([
            "first",
            "    four spaces",
            "\ttab indentation",
            "  mixed\tindentation",
            "last"
        ]);

        var context = Assert.IsType<SourceContext>(SourceContextReader.TryRead(source.Path, 3));

        Assert.Equal(
            ["first", "    four spaces", "\ttab indentation", "  mixed\tindentation", "last"],
            context.Lines.Select(line => line.Text));
        var current = Assert.Single(context.Lines, line => line.IsCurrent);
        Assert.Equal(3, current.Number);
        Assert.Equal(context.CurrentLine, current.Number);
    }

    [Fact]
    public void TryRead_TruncatesOnlyLinesLongerThanFiveHundredCharacters()
    {
        var exactlyMaximum = new string('a', 500);
        var overMaximum = new string('b', 501);
        using var source = SourceFile.Create([exactlyMaximum, overMaximum]);

        var context = Assert.IsType<SourceContext>(SourceContextReader.TryRead(source.Path, 2));

        Assert.Equal(exactlyMaximum, context.Lines[0].Text);
        Assert.Equal(new string('b', 500) + "...", context.Lines[1].Text);
        Assert.Equal(503, context.Lines[1].Text.Length);
    }

    [Fact]
    public void TryRead_Utf8BomFile_DecodesTextWithoutReturningTheBom()
    {
        using var source = SourceFile.Create(
            ["caf\u00e9", "\u03bb expression"],
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var context = Assert.IsType<SourceContext>(SourceContextReader.TryRead(source.Path, 1));

        Assert.Equal("caf\u00e9", context.Lines[0].Text);
        Assert.Equal("\u03bb expression", context.Lines[1].Text);
        Assert.DoesNotContain('\uFEFF', context.Lines[0].Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public void TryRead_InvalidCurrentLine_ReturnsNull(int? currentLine)
    {
        using var source = SourceFile.Create(["only line"]);

        Assert.Null(SourceContextReader.TryRead(source.Path, currentLine));
    }

    [Fact]
    public void TryRead_CurrentLinePastEndOfFile_ReturnsNull()
    {
        using var source = SourceFile.Create(["first", "second"]);

        Assert.Null(SourceContextReader.TryRead(source.Path, 3));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/source.cs")]
    public void TryRead_InvalidOrRelativePath_ReturnsNull(string? sourcePath)
    {
        Assert.Null(SourceContextReader.TryRead(sourcePath, 1));
    }

    [Fact]
    public void TryRead_MissingFile_ReturnsNull()
    {
        var missingPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"csdbg-missing-source-{Guid.NewGuid():N}.cs");

        Assert.Null(SourceContextReader.TryRead(missingPath, 1));
    }

    [Fact]
    public void TryRead_DirectoryPath_ReturnsNull()
    {
        using var directory = new TemporaryDirectory();

        Assert.Null(SourceContextReader.TryRead(directory.Path, 1));
    }

    [Fact]
    public void TryRead_UnreadableFileOnUnix_ReturnsNull()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var source = SourceFile.Create(["secret"]);
        var originalMode = File.GetUnixFileMode(source.Path);
        try
        {
            File.SetUnixFileMode(source.Path, UnixFileMode.None);

            Assert.Null(SourceContextReader.TryRead(source.Path, 1));
        }
        finally
        {
            File.SetUnixFileMode(source.Path, originalMode);
        }
    }

    [Fact]
    public void TryRead_FileLargerThanTwoMiB_ReturnsNull()
    {
        using var source = SourceFile.CreateBytes(new byte[(2 * 1024 * 1024) + 1]);

        Assert.Null(SourceContextReader.TryRead(source.Path, 1));
    }

    private static string[] NumberedLines(int count) =>
        Enumerable.Range(1, count).Select(number => $"line {number}").ToArray();

    private static void AssertContext(
        SourceContext? context,
        int expectedStart,
        int expectedEnd,
        int expectedCurrent,
        int[] expectedLineNumbers)
    {
        var actual = Assert.IsType<SourceContext>(context);
        Assert.Equal(expectedStart, actual.StartLine);
        Assert.Equal(expectedEnd, actual.EndLine);
        Assert.Equal(expectedCurrent, actual.CurrentLine);
        Assert.Equal(expectedLineNumbers, actual.Lines.Select(line => line.Number));
        Assert.Equal(
            expectedLineNumbers.Select(number => $"line {number}"),
            actual.Lines.Select(line => line.Text));

        var current = Assert.Single(actual.Lines, line => line.IsCurrent);
        Assert.Equal(expectedCurrent, current.Number);
    }

    private sealed class SourceFile : IDisposable
    {
        private SourceFile(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static SourceFile Create(string[] lines, Encoding? encoding = null)
        {
            var source = CreateBytes([]);
            File.WriteAllLines(source.Path, lines, encoding ?? new UTF8Encoding(false));
            return source;
        }

        public static SourceFile CreateBytes(byte[] contents)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"csdbg-source-context-{Guid.NewGuid():N}.cs");
            File.WriteAllBytes(path, contents);
            return new SourceFile(path);
        }

        public void Dispose()
        {
            File.Delete(Path);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"csdbg-source-context-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
