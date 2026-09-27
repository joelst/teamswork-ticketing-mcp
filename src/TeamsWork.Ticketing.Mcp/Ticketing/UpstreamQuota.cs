using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using TeamsWork.Ticketing.Mcp.Configuration;

namespace TeamsWork.Ticketing.Mcp.Ticketing;

/// <summary>Who the current upstream request is charged to. Null means nobody in particular (a single-user mode).</summary>
public interface IUpstreamCaller
{
    string? Key { get; }
}

/// <summary>stdio and local HTTP have one user, so there is nobody to be fair between.</summary>
public sealed class SingleUserUpstreamCaller : IUpstreamCaller
{
    public string? Key => null;
}

/// <summary>
/// The authenticated caller of the current HTTP request, keyed like the per-caller request limit. A request with no
/// authenticated caller (which the endpoint's authorization shouldn't let through) shares one key rather than going
/// unlimited, so the quota fails closed.
/// </summary>
public sealed class HttpUpstreamCaller : IUpstreamCaller
{
    internal const string UnidentifiedCaller = "unidentified";

    private readonly IHttpContextAccessor _accessor;

    public HttpUpstreamCaller(IHttpContextAccessor accessor) => _accessor = accessor;

    public string? Key => _accessor.HttpContext is { User.Identity.IsAuthenticated: true } context
        ? RequestLimits.CallerKey(context.User, context.Connection.RemoteIpAddress)
        : UnidentifiedCaller;
}

/// <summary>
/// Each caller's share of the upstream quota. One tool call can make several upstream requests (a scan, a lookup with
/// fallbacks, retries), while the HTTP per-caller limit counts tool calls, so without this one caller could use up
/// the vendor's 100 requests a minute that every caller shares. Charged per upstream request, before the process-wide
/// limiter, and refused at once rather than queued, so a busy caller can't hold other callers' place in the queue.
/// The caller's permit is taken before the process-wide one, so a request refused by the process-wide limiter still
/// uses one of the caller's permits: overcharging the caller is the safe way round, since the reverse would spend a
/// permit every caller shares.
/// </summary>
public sealed class UpstreamQuota : IDisposable
{
    private readonly PartitionedRateLimiter<string>? _limiter;
    private readonly int _perMinute;

    public UpstreamQuota(IOptions<TicketingOptions> options)
    {
        _perMinute = options.Value.MaxUpstreamRequestsPerCallerPerMinute;
        if (_perMinute > 0)
        {
            _limiter = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetSlidingWindowLimiter(key, _ =>
                new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = _perMinute,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
        }
    }

    /// <summary>Takes one permit for <paramref name="callerKey"/>, or throws when that caller's share is used up.</summary>
    public RateLimitLease? Acquire(string? callerKey)
    {
        if (_limiter is null || callerKey is null)
        {
            return null;
        }

        RateLimitLease lease = _limiter.AttemptAcquire(callerKey);
        if (lease.IsAcquired)
        {
            return lease;
        }

        lease.Dispose();
        throw new TicketingApiException(
            $"You have used your share of the Ticketing API quota ({_perMinute} requests a minute per caller). Wait a minute and " +
            "retry. Tools that read many tickets (list_my_tickets, list_sla_risk, count_tickets, find_ticket_by_number) use several " +
            "requests each; narrow them with filters.")
        {
            QuotaRefused = true,
        };
    }

    public void Dispose() => _limiter?.Dispose();
}
