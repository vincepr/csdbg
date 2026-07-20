using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Csdbg.Core.Dap;

namespace Csdbg.Core.Tests;

public sealed class DapMessageFramingTests
{
    [Fact]
    public async Task WriteAsync_UsesUtf8ByteLengthForUnicodePayload()
    {
        var message = new JsonObject
        {
            ["type"] = "event",
            ["message"] = "Gruesse aus Koeln \u2603"
        };
        var expectedPayload = JsonSerializer.SerializeToUtf8Bytes(message);
        await using var stream = new MemoryStream();

        await DapMessageFraming.WriteAsync(stream, message);

        var frame = stream.ToArray();
        var separator = FindHeaderSeparator(frame);
        var header = Encoding.ASCII.GetString(frame, 0, separator);
        var payloadOffset = separator + 4;
        Assert.Equal($"Content-Length: {expectedPayload.Length}", header);
        Assert.Equal(expectedPayload.Length, frame.Length - payloadOffset);
        Assert.Equal(expectedPayload, frame[payloadOffset..]);
    }

    [Fact]
    public async Task ReadAsync_ReadsMultipleSequentialFrames()
    {
        var first = new JsonObject { ["seq"] = 1, ["name"] = "first" };
        var second = new JsonObject { ["seq"] = 2, ["name"] = "zweite \u2603" };
        await using var stream = new MemoryStream();
        await DapMessageFraming.WriteAsync(stream, first);
        await DapMessageFraming.WriteAsync(stream, second);
        stream.Position = 0;

        var firstResult = await DapMessageFraming.ReadAsync(stream);
        var secondResult = await DapMessageFraming.ReadAsync(stream);
        var endResult = await DapMessageFraming.ReadAsync(stream);

        Assert.NotNull(firstResult);
        Assert.NotNull(secondResult);
        Assert.True(JsonNode.DeepEquals(first, firstResult));
        Assert.True(JsonNode.DeepEquals(second, secondResult));
        Assert.Null(endResult);
    }

    [Theory]
    [InlineData("Content-Length: nope\r\n\r\n")]
    [InlineData("Content-Length: -1\r\n\r\n")]
    public async Task ReadAsync_RejectsMalformedContentLength(string frame)
    {
        await using var stream = Utf8Stream(frame);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => DapMessageFraming.ReadAsync(stream));

        Assert.Contains("Content-Length", exception.Message);
    }

    [Fact]
    public async Task ReadAsync_RejectsContentLengthAboveMaximumBeforeReadingPayload()
    {
        var oversizedLength = DapMessageFraming.MaximumPayloadLength + 1;
        await using var stream = Utf8Stream($"Content-Length: {oversizedLength}\r\n\r\nx");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => DapMessageFraming.ReadAsync(stream));

        Assert.Contains("limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            stream.Position < stream.Length,
            "Oversized frames must be rejected before consuming any payload bytes.");
    }

    [Fact]
    public async Task ReadAsync_RejectsMissingContentLength()
    {
        await using var stream = Utf8Stream("Content-Type: application/json\r\n\r\n{}");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => DapMessageFraming.ReadAsync(stream));

        Assert.Contains("missing", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_RejectsTruncatedPayload()
    {
        await using var stream = Utf8Stream("Content-Length: 20\r\n\r\n{\"seq\":1}");

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => DapMessageFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task ReadAsync_RejectsJsonPayloadThatIsNotAnObject()
    {
        await using var stream = Utf8Stream("Content-Length: 4\r\n\r\nnull");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => DapMessageFraming.ReadAsync(stream));

        Assert.Contains("JSON object", exception.Message);
    }

    private static MemoryStream Utf8Stream(string value)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(value));
    }

    private static int FindHeaderSeparator(byte[] frame)
    {
        ReadOnlySpan<byte> separator = "\r\n\r\n"u8;
        return frame.AsSpan().IndexOf(separator);
    }
}
