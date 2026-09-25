using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using TeamsWork.Ticketing.Mcp.Configuration;

namespace TeamsWork.Ticketing.Mcp.Ticketing;

/// <summary>
/// Process-wide sliding-window limiter that keeps this server under the vendor's 100 requests / 60 s quota.
/// Calls beyond the quota wait briefly (bounded queue) and then fail with a clear message instead of
/// letting the upstream API return 429s that would look like transient failures to an agent.
/// </summary>
public sealed class TicketingRateLimiter : IDisposable
{
    private readonly SlidingWindowRateLimiter _limiter;

    public TicketingRateLimiter(IOptions<TicketingOptions> options)
    {
        var o = options.Value;
        _limiter = new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
        {
            PermitLimit = o.RateLimitPermits,
            Window = TimeSpan.FromSeconds(o.RateLimitWindowSeconds),
            SegmentsPerWindow = 6,
            QueueLimit = 25,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });
    }

    /// <summary>Acquires one permit or throws <see cref="TicketingApiException"/> when the quota is exhausted.</summary>
    public async ValueTask<RateLimitLease> AcquireAsync(CancellationToken cancellationToken)
    {
        RateLimitLease lease = await _limiter.AcquireAsync(1, cancellationToken);
        if (!lease.IsAcquired)
        {
            lease.Dispose();
            throw new TicketingApiException(
                "The Ticketing API rate limit (100 requests per 60 seconds) has been reached. Wait a minute and retry, " +
                "or request fewer / larger pages.");
        }

        return lease;
    }

    public void Dispose() => _limiter.Dispose();
}
