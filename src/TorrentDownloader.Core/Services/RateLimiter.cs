namespace TorrentDownloader.Core.Services;

/// <summary>
/// A simple token-bucket rate limiter: consumers ask to spend N bytes and wait until enough
/// budget has refilled. 0 (or negative) means unlimited - no waiting at all.
/// </summary>
public sealed class RateLimiter
{
    private readonly object _lock = new();
    private long _bytesPerSecond;
    private double _availableBytes;
    private DateTime _lastRefillUtc = DateTime.UtcNow;

    public void SetLimitBytesPerSecond(long bytesPerSecond)
    {
        lock (_lock)
        {
            _bytesPerSecond = bytesPerSecond;
            // Reset the burst allowance to exactly one second's worth so a limit change takes
            // effect immediately rather than inheriting whatever balance the old limit left behind.
            _availableBytes = bytesPerSecond;
            _lastRefillUtc = DateTime.UtcNow;
        }
    }

    public async Task ThrottleAsync(int byteCount, CancellationToken token)
    {
        while (true)
        {
            TimeSpan waitTime;
            lock (_lock)
            {
                if (_bytesPerSecond <= 0)
                    return;

                Refill();

                if (_availableBytes >= byteCount)
                {
                    _availableBytes -= byteCount;
                    return;
                }

                var shortfall = byteCount - _availableBytes;
                waitTime = TimeSpan.FromSeconds(shortfall / _bytesPerSecond);
            }

            if (waitTime > TimeSpan.Zero)
                await Task.Delay(waitTime, token).ConfigureAwait(false);
        }
    }

    private void Refill()
    {
        var now = DateTime.UtcNow;
        var elapsedSeconds = (now - _lastRefillUtc).TotalSeconds;
        if (elapsedSeconds <= 0)
            return;

        _availableBytes = Math.Min(_bytesPerSecond, _availableBytes + elapsedSeconds * _bytesPerSecond);
        _lastRefillUtc = now;
    }
}
