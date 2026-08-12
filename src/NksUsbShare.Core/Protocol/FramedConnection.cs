namespace NksUsbShare.Core.Protocol;

public readonly record struct Frame(FrameHeader Header, byte[] Payload)
{
    public MessageType Type => Header.Type;
    public uint Sequence => Header.Sequence;
}

/// <summary>
/// Length-prefixed message framing over any duplex stream (in practice an <see cref="System.Net.Security.SslStream"/>).
/// Writes are serialised so URB traffic from several tasks cannot interleave mid-frame.
/// </summary>
public sealed class FramedConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly bool _ownsStream;
    private uint _nextSequence;
    private int _disposed;

    public FramedConnection(Stream stream, bool ownsStream = true)
    {
        _stream = stream;
        _ownsStream = ownsStream;
    }

    public uint NextSequence() => Interlocked.Increment(ref _nextSequence);

    public async Task SendAsync(MessageType type, byte[] payload, uint sequence, CancellationToken cancellationToken)
    {
        if (payload.Length > FrameHeader.MaxPayloadLength)
            throw new ProtocolViolationExceptionEx($"Refusing to send {payload.Length} bytes; cap is {FrameHeader.MaxPayloadLength}.");

        var header = new FrameHeader(FrameHeader.CurrentVersion, type, sequence, (uint)payload.Length);
        var buffer = new byte[FrameHeader.Size + payload.Length];
        header.WriteTo(buffer);
        payload.CopyTo(buffer.AsSpan(FrameHeader.Size));

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task SendAsync(MessageType type, byte[] payload, CancellationToken cancellationToken)
        => SendAsync(type, payload, NextSequence(), cancellationToken);

    /// <summary>Reads one whole frame. Returns null on a clean end of stream.</summary>
    public async Task<Frame?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var headerBuffer = new byte[FrameHeader.Size];
        if (!await ReadExactOrEofAsync(headerBuffer, cancellationToken).ConfigureAwait(false))
            return null;

        var header = FrameHeader.ReadFrom(headerBuffer);
        if (header.Version != FrameHeader.CurrentVersion)
            throw new ProtocolViolationExceptionEx($"Peer speaks protocol version {header.Version}, this build speaks {FrameHeader.CurrentVersion}.");

        var payload = header.PayloadLength == 0 ? [] : new byte[header.PayloadLength];
        if (payload.Length > 0 && !await ReadExactOrEofAsync(payload, cancellationToken).ConfigureAwait(false))
            throw new ProtocolViolationExceptionEx("Stream ended in the middle of a payload.");

        return new Frame(header, payload);
    }

    private async Task<bool> ReadExactOrEofAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) return offset != 0
                ? throw new ProtocolViolationExceptionEx("Stream ended mid-frame.")
                : false;
            offset += read;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _writeLock.Dispose();
        if (_ownsStream) await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
