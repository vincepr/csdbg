using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using Csdbg.Core.Dap;

namespace Csdbg.Core.Tests;

internal sealed class ScriptedDapProcessFactory(ScriptedDapProcess process) : IDapProcessFactory
{
    public int StartCount { get; private set; }

    public IDapProcess Start(string executablePath)
    {
        StartCount++;
        return process;
    }
}

internal sealed class ScriptedDapProcess : IDapProcess
{
    private readonly Pipe _standardInput = new();
    private readonly Pipe _standardOutput = new();
    private readonly Pipe _standardError = new(new PipeOptions(
        pauseWriterThreshold: 4096,
        resumeWriterThreshold: 2048,
        useSynchronizationContext: false));
    private readonly Stream _adapterInput;
    private readonly Stream _adapterOutput;
    private readonly Stream _adapterError;
    private readonly StreamReader _standardErrorReader;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _hasExited;
    private int _disposed;
    private int _killCount;
    private int _disposeCount;

    public ScriptedDapProcess()
    {
        _adapterInput = _standardInput.Reader.AsStream();
        _adapterOutput = _standardOutput.Writer.AsStream();
        _adapterError = _standardError.Writer.AsStream();
        StandardInput = new FaultInjectingWriteStream(_standardInput.Writer.AsStream());
        StandardOutput = _standardOutput.Reader.AsStream();
        _standardErrorReader = new StreamReader(
            _standardError.Reader.AsStream(),
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
    }

    public Stream StandardInput { get; }
    public Stream StandardOutput { get; }
    public TextReader StandardError => _standardErrorReader;
    public bool HasExited => Volatile.Read(ref _hasExited) != 0;
    public int KillCount => Volatile.Read(ref _killCount);
    public int DisposeCount => Volatile.Read(ref _disposeCount);
    public int InputWriteCount => ((FaultInjectingWriteStream)StandardInput).WriteCount;

    public void FailNextInputWrite(Exception exception)
    {
        ((FaultInjectingWriteStream)StandardInput).FailNextWrite(exception);
    }

    public Task BlockNextInputWriteUntilCanceled()
    {
        return ((FaultInjectingWriteStream)StandardInput).BlockNextWriteUntilCanceled();
    }

    public Task<JsonObject?> ReadRequestAsync(CancellationToken cancellationToken = default)
    {
        return DapMessageFraming.ReadAsync(_adapterInput, cancellationToken);
    }

    public Task SendResponseAsync(JsonObject request, CancellationToken cancellationToken = default)
    {
        return SendResponseAsync(request, success: true, message: null, cancellationToken);
    }

    public Task SendResponseAsync(
        JsonObject request,
        bool success,
        string? message = null,
        CancellationToken cancellationToken = default)
    {
        var response = new JsonObject
        {
            ["seq"] = 1000 + (request["seq"]?.GetValue<int>() ?? 0),
            ["type"] = "response",
            ["request_seq"] = request["seq"]?.GetValue<int>() ?? 0,
            ["command"] = request["command"]?.GetValue<string>(),
            ["success"] = success,
            ["body"] = new JsonObject()
        };
        if (message is not null)
        {
            response["message"] = message;
        }

        return DapMessageFraming.WriteAsync(_adapterOutput, response, cancellationToken);
    }

    public async Task WriteRawOutputAsync(
        string value,
        CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        await _adapterOutput.WriteAsync(bytes, cancellationToken);
        await _adapterOutput.FlushAsync(cancellationToken);
    }

    public async Task WriteStandardErrorAsync(string value, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        await _adapterError.WriteAsync(bytes, cancellationToken);
        await _adapterError.FlushAsync(cancellationToken);
    }

    public async Task CompleteOutputAsync()
    {
        await _adapterOutput.DisposeAsync();
    }

    public void Kill()
    {
        Interlocked.Increment(ref _killCount);
        Exit();
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        return _exited.Task.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _disposeCount);
        Exit();
        StandardInput.Dispose();
        StandardOutput.Dispose();
        _adapterInput.Dispose();
        _adapterOutput.Dispose();
        _adapterError.Dispose();
        _standardErrorReader.Dispose();
    }

    private void Exit()
    {
        if (Interlocked.Exchange(ref _hasExited, 1) != 0)
        {
            return;
        }

        _standardInput.Writer.Complete();
        _standardInput.Reader.Complete();
        _standardOutput.Writer.Complete();
        _standardOutput.Reader.Complete();
        _standardError.Writer.Complete();
        _standardError.Reader.Complete();
        _exited.TrySetResult();
    }

    private sealed class FaultInjectingWriteStream(Stream inner) : Stream
    {
        private Exception? _nextWriteException;
        private TaskCompletionSource? _nextBlockedWrite;
        private int _writeCount;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public int WriteCount => Volatile.Read(ref _writeCount);
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void FailNextWrite(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (Interlocked.CompareExchange(ref _nextWriteException, exception, null) is not null)
            {
                throw new InvalidOperationException("An input write failure is already configured.");
            }
        }

        public Task BlockNextWriteUntilCanceled()
        {
            var writeStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _nextBlockedWrite, writeStarted, null) is not null)
            {
                throw new InvalidOperationException("An input write block is already configured.");
            }

            return writeStarted.Task;
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writeCount);
            var exception = Interlocked.Exchange(ref _nextWriteException, null);
            if (exception is not null)
            {
                throw exception;
            }

            var blockedWrite = Interlocked.Exchange(ref _nextBlockedWrite, null);
            if (blockedWrite is not null)
            {
                blockedWrite.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Interlocked.Increment(ref _writeCount);
            var exception = Interlocked.Exchange(ref _nextWriteException, null);
            if (exception is not null)
            {
                throw exception;
            }

            inner.Write(buffer, offset, count);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();
    }
}
