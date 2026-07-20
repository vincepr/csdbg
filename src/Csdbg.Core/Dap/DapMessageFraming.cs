using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Csdbg.Core.Dap;

public static class DapMessageFraming
{
    public const int MaximumPayloadLength = 16 * 1024 * 1024;
    private const int MaximumHeaderLineLength = 8192;

    public static async Task WriteAsync(
        Stream stream,
        JsonObject message,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        var frame = new byte[header.Length + payload.Length];
        header.CopyTo(frame, 0);
        payload.CopyTo(frame, header.Length);

        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<JsonObject?> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        int? contentLength = null;
        var readAnyHeader = false;

        while (true)
        {
            var line = await ReadAsciiLineAsync(stream, cancellationToken);
            if (line is null)
            {
                if (!readAnyHeader)
                {
                    return null;
                }

                throw new EndOfStreamException("DAP header ended before the blank separator line.");
            }

            readAnyHeader = true;
            if (line.Length == 0)
            {
                break;
            }

            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line["Content-Length:".Length..].Trim();
            if (!int.TryParse(value, out var parsedLength) || parsedLength < 0)
            {
                throw new InvalidDataException($"Invalid DAP Content-Length value: {value}");
            }

            if (parsedLength > MaximumPayloadLength)
            {
                throw new InvalidDataException(
                    $"DAP Content-Length exceeds the {MaximumPayloadLength}-byte limit.");
            }

            contentLength = parsedLength;
        }

        if (contentLength is null)
        {
            throw new InvalidDataException("DAP message is missing a Content-Length header.");
        }

        var payload = new byte[contentLength.Value];
        await stream.ReadExactlyAsync(payload, cancellationToken);

        try
        {
            var node = JsonNode.Parse(payload);
            return node as JsonObject
                ?? throw new InvalidDataException("DAP payload must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("DAP payload contains invalid JSON.", ex);
        }
    }

    private static async Task<string?> ReadAsciiLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        while (true)
        {
            var buffer = new byte[1];
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            }

            if (buffer[0] == (byte)'\n')
            {
                if (bytes.Count > 0 && bytes[^1] == (byte)'\r')
                {
                    bytes.RemoveAt(bytes.Count - 1);
                }

                return Encoding.ASCII.GetString(bytes.ToArray());
            }

            bytes.Add(buffer[0]);
            if (bytes.Count > MaximumHeaderLineLength)
            {
                throw new InvalidDataException("DAP header line exceeds the supported length.");
            }
        }
    }
}
