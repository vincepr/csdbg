using System.Text;

namespace Csdbg.Core;

public sealed record SourceContextLine(int Number, string Text, bool IsCurrent);

public sealed record SourceContext(
    int StartLine,
    int EndLine,
    int CurrentLine,
    SourceContextLine[] Lines);

public static class SourceContextReader
{
    private const int ContextRadius = 3;
    private const int MaximumLineCharacters = 500;
    private const long MaximumFileBytes = 2L * 1024 * 1024;

    public static SourceContext? TryRead(string? sourcePath, int? currentLine)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)
            || currentLine is null or <= 0
            || !Path.IsPathFullyQualified(sourcePath))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            if (stream.Length > MaximumFileBytes)
            {
                return null;
            }

            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096,
                leaveOpen: false);
            var startLine = Math.Max(1, currentLine.Value - ContextRadius);
            var requestedEndLine = currentLine.Value + ContextRadius;
            var lines = new List<SourceContextLine>(ContextRadius * 2 + 1);
            var lineNumber = 0;
            while (lineNumber < requestedEndLine && reader.ReadLine() is { } text)
            {
                lineNumber++;
                if (lineNumber >= startLine)
                {
                    lines.Add(new SourceContextLine(
                        lineNumber,
                        Truncate(text),
                        lineNumber == currentLine.Value));
                }
            }

            if (lines.Count == 0 || lines.All(line => !line.IsCurrent))
            {
                return null;
            }

            return new SourceContext(
                lines[0].Number,
                lines[^1].Number,
                currentLine.Value,
                lines.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Truncate(string value) =>
        value.Length <= MaximumLineCharacters
            ? value
            : $"{value[..MaximumLineCharacters]}...";
}
