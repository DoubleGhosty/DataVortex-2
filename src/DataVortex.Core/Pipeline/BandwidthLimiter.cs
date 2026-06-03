namespace DataVortex.Core.Pipeline;

/// <summary>
/// Global token-bucket rate limiter shared by every download stream. A limit of 0 means unlimited.
/// Thread-safe; a caller awaits <see cref="ThrottleAsync"/> for the number of bytes it is about to write.
/// </summary>
public sealed class BandwidthLimiter
{
    private readonly object _gate = new();
    private long _bytesPerSecond;   // 0 = unlimited
    private double _tokens;
    private long _lastRefillMs;

    public BandwidthLimiter(long bytesPerSecond)
    {
        _bytesPerSecond = bytesPerSecond;
        _tokens = bytesPerSecond;
        _lastRefillMs = Environment.TickCount64;
    }

    public long BytesPerSecond
    {
        get { lock (_gate) return _bytesPerSecond; }
        set
        {
            lock (_gate)
            {
                _bytesPerSecond = value;
                if (value > 0) _tokens = Math.Min(_tokens, value);
            }
        }
    }

    public async Task ThrottleAsync(int bytes, CancellationToken ct)
    {
        while (true)
        {
            int waitMs;
            lock (_gate)
            {
                if (_bytesPerSecond <= 0) return;       // unlimited
                Refill();

                // A single write larger than one second's budget is capped so we never stall forever.
                var need = Math.Min(bytes, _bytesPerSecond);
                if (_tokens >= need)
                {
                    _tokens -= need;
                    return;
                }

                var deficit = need - _tokens;
                waitMs = Math.Clamp((int)Math.Ceiling(deficit / _bytesPerSecond * 1000.0), 1, 1000);
            }
            await Task.Delay(waitMs, ct).ConfigureAwait(false);
        }
    }

    private void Refill()
    {
        var now = Environment.TickCount64;
        var elapsedSec = (now - _lastRefillMs) / 1000.0;
        if (elapsedSec <= 0) return;
        _lastRefillMs = now;
        _tokens = Math.Min(_bytesPerSecond, _tokens + elapsedSec * _bytesPerSecond);
    }
}
