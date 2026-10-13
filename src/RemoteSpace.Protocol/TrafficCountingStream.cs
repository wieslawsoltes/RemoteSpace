using RemoteSpace.Core;

namespace RemoteSpace.Protocol;

/// <summary>Pass-through stream instrumentation. Only completed reads/writes are counted.
/// Place beneath TLS to measure TCP-stream bytes including security traffic, excluding
/// TCP/IP headers and retransmits. The wrapper never reads ahead or copies payloads.</summary>
public sealed class TrafficCountingStream : Stream
{
    private readonly Stream _inner;
    private readonly SessionTrafficCounter _traffic;
    private readonly bool _leaveOpen;
    private int _disposed;
    public TrafficCountingStream(Stream inner, SessionTrafficCounter traffic, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(inner); ArgumentNullException.ThrowIfNull(traffic);
        _inner = inner; _traffic = traffic; _leaveOpen = leaveOpen;
    }
    private void Check() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    public override bool CanRead => _disposed == 0 && _inner.CanRead;
    public override bool CanSeek => _disposed == 0 && _inner.CanSeek;
    public override bool CanWrite => _disposed == 0 && _inner.CanWrite;
    public override bool CanTimeout => _inner.CanTimeout;
    public override long Length { get { Check(); return _inner.Length; } }
    public override long Position { get { Check(); return _inner.Position; } set { Check(); _inner.Position = value; } }
    public override int ReadTimeout { get => _inner.ReadTimeout; set => _inner.ReadTimeout = value; }
    public override int WriteTimeout { get => _inner.WriteTimeout; set => _inner.WriteTimeout = value; }
    public override long Seek(long offset, SeekOrigin origin) { Check(); return _inner.Seek(offset, origin); }
    public override void SetLength(long value) { Check(); _inner.SetLength(value); }
    public override void Flush() { Check(); _inner.Flush(); }
    public override Task FlushAsync(CancellationToken cancellationToken) { Check(); return _inner.FlushAsync(cancellationToken); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        Check(); int read = _inner.Read(buffer, offset, count); _traffic.AddReceived(read); return read;
    }
    public override int Read(Span<byte> buffer)
    {
        Check(); int read = _inner.Read(buffer); _traffic.AddReceived(read); return read;
    }
    public override int ReadByte()
    {
        Check(); int value = _inner.ReadByte(); if (value >= 0) _traffic.AddReceived(1); return value;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Check(); var pending = _inner.ReadAsync(buffer, cancellationToken);
        if (!pending.IsCompletedSuccessfully) return FinishReadAsync(pending);
        int count = pending.Result; _traffic.AddReceived(count); return new(count);
    }
    private async ValueTask<int> FinishReadAsync(ValueTask<int> pending)
    {
        int count = await pending.ConfigureAwait(false); _traffic.AddReceived(count); return count;
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        Check(); _inner.Write(buffer, offset, count); _traffic.AddSent(count);
    }
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Check(); _inner.Write(buffer); _traffic.AddSent(buffer.Length);
    }
    public override void WriteByte(byte value) { Check(); _inner.WriteByte(value); _traffic.AddSent(1); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Check(); var pending = _inner.WriteAsync(buffer, cancellationToken);
        if (!pending.IsCompletedSuccessfully) return FinishWriteAsync(pending, buffer.Length);
        pending.GetAwaiter().GetResult(); _traffic.AddSent(buffer.Length); return ValueTask.CompletedTask;
    }
    private async ValueTask FinishWriteAsync(ValueTask pending, int count)
    {
        await pending.ConfigureAwait(false); _traffic.AddSent(count);
    }
    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && disposing && !_leaveOpen) _inner.Dispose();
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && !_leaveOpen) await _inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
