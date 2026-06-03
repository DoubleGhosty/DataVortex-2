namespace DataVortex.Core.Pipeline;

/// <summary>
/// Transparent stream decorator that (a) enforces a global write bandwidth budget via
/// <see cref="BandwidthLimiter"/> and (b) reports cumulative bytes written through an
/// <see cref="IProgress{T}"/>. All non-write members delegate to the inner stream, so it is safe to
/// hand to WTelegramClient regardless of how it positions/sizes the underlying file.
/// </summary>
public sealed class ThrottledStream : Stream
{
    private readonly Stream _inner;
    private readonly BandwidthLimiter _limiter;
    private readonly IProgress<long>? _progress;
    private readonly Action<long>? _onBytesWritten;
    private long _written;

    public ThrottledStream(Stream inner, BandwidthLimiter limiter, IProgress<long>? progress = null,
        Action<long>? onBytesWritten = null)
    {
        _inner = inner;
        _limiter = limiter;
        _progress = progress;
        _onBytesWritten = onBytesWritten;
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        await _limiter.ThrottleAsync(count, ct).ConfigureAwait(false);
        await _inner.WriteAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false);
        Advance(count);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        => new(WriteAsyncCore(buffer, ct));

    private async Task WriteAsyncCore(ReadOnlyMemory<byte> buffer, CancellationToken ct)
    {
        await _limiter.ThrottleAsync(buffer.Length, ct).ConfigureAwait(false);
        await _inner.WriteAsync(buffer, ct).ConfigureAwait(false);
        Advance(buffer.Length);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _limiter.ThrottleAsync(count, CancellationToken.None).GetAwaiter().GetResult();
        _inner.Write(buffer, offset, count);
        Advance(count);
    }

    private void Advance(int count)
    {
        _written += count;
        _progress?.Report(_written);
        _onBytesWritten?.Invoke(count);
    }

    // ---- transparent delegation ----
    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
