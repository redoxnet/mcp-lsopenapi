using System.Collections.Concurrent;

namespace RedoxNet.LsOpenApi.Core.Http;

/// <summary>
/// Throttles concurrent TR calls so each TR code stays within its published
/// per-second rate limit.
/// </summary>
/// <remarks>
/// The limiter is a simple per-TR minimum-spacing gate. It does not
/// pre-allocate buckets; only TRs actually called incur state. When the
/// rate limit is unknown or zero/negative the limiter is a no-op for that TR.
/// </remarks>
public sealed class TrRateLimiter
{
    readonly ConcurrentDictionary<string, TrSlot> _slots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Waits until it is safe to issue another call for <paramref name="trCode"/>.
    /// </summary>
    /// <param name="trCode">TR code.</param>
    /// <param name="ratePerSecond">Published rate limit (calls/sec). Non-positive values disable throttling.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task WaitAsync(string trCode, int? ratePerSecond, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trCode);

        if (ratePerSecond is not > 0)
            return;

        TimeSpan minGap = TimeSpan.FromMilliseconds(1000.0 / ratePerSecond.Value);
        TrSlot slot = _slots.GetOrAdd(trCode, static _ => new TrSlot());

        await slot.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset earliest = slot.LastIssuedUtc + minGap;
            if (earliest > now)
            {
                TimeSpan wait = earliest - now;
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                now = DateTimeOffset.UtcNow;
            }
            slot.LastIssuedUtc = now;
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    sealed class TrSlot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset LastIssuedUtc { get; set; }
    }
}
