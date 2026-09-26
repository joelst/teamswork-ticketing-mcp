using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;

namespace TeamsWork.Ticketing.Mcp.Configuration;

/// <summary>
/// Limits on what one HTTP request, and one caller, can make the server do. The container is small (0.5 GiB) and
/// every caller shares one upstream quota, so these bound both memory and fairness.
/// </summary>
internal static class RequestLimits
{
    public const string PerCallerPolicy = "PerCaller";

    /// <summary>
    /// Largest request body accepted. A tool call is a few kilobytes; the largest legitimate one (a 20,000-character
    /// description plus custom fields) is well under 1 MiB.
    /// </summary>
    public static int MaxRequestBodyBytes(IConfiguration configuration)
    {
        int bytes = StartupConfigurationException.ReadSetting(
            () => configuration.GetValue<int?>("Mcp:MaxRequestBodyBytes"), "Mcp:MaxRequestBodyBytes") ?? 1024 * 1024;
        return bytes is >= 16 * 1024 and <= 64 * 1024 * 1024
            ? bytes
            : throw new StartupConfigurationException("Mcp:MaxRequestBodyBytes must be between 16384 and 67108864.");
    }

    /// <summary>
    /// Requests one caller may make per minute. The upstream allows 100 calls a minute for everyone together, so the
    /// default of 60 leaves room for others when one agent runs away. 0 turns the limit off.
    /// </summary>
    public static int RequestsPerMinutePerCaller(IConfiguration configuration)
    {
        int limit = StartupConfigurationException.ReadSetting(
            () => configuration.GetValue<int?>("Mcp:RequestsPerMinutePerCaller"), "Mcp:RequestsPerMinutePerCaller") ?? 60;
        return limit is >= 0 and <= 100_000
            ? limit
            : throw new StartupConfigurationException("Mcp:RequestsPerMinutePerCaller must be between 0 (no limit) and 100000.");
    }

    /// <summary>Registers the per-caller policy. Callers are told when to retry with a Retry-After header.</summary>
    public static void AddPerCallerRateLimiting(IServiceCollection services, int requestsPerMinute)
    {
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (context, _) =>
            {
                TimeSpan retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan wait) ? wait : TimeSpan.FromMinutes(1);
                context.HttpContext.Response.Headers.RetryAfter =
                    Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };
            o.AddPolicy(PerCallerPolicy, context => requestsPerMinute == 0
                ? RateLimitPartition.GetNoLimiter("unlimited")
                : RateLimitPartition.GetSlidingWindowLimiter(CallerKey(context.User, context.Connection.RemoteIpAddress), _ =>
                    new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = requestsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 6,
                        QueueLimit = 0,
                    }));
        });
    }

    /// <summary>
    /// The identity a request's quota is charged to: the tenant and object ID of the user or application from the
    /// validated token. The policy only runs after authorization, so the address fallback is for completeness.
    /// </summary>
    internal static string CallerKey(ClaimsPrincipal user, System.Net.IPAddress? remoteAddress)
    {
        string? tenant = user.FindFirst("tid")?.Value ?? user.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;
        string? id = user.FindFirst("oid")?.Value
                     ?? user.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
                     ?? user.FindFirst("azp")?.Value
                     ?? user.FindFirst("appid")?.Value;
        return id is not null ? $"{tenant}/{id}" : $"address/{remoteAddress}";
    }

    /// <summary>
    /// Enforces the body limit. Kestrel's MaxRequestBodySize covers bodies of any encoding in production; this also
    /// answers a declared oversized body with 413 before anything reads it, including under the in-process test host.
    /// </summary>
    public static Task RejectOversizedBodiesAsync(HttpContext context, RequestDelegate next, int maxBytes)
    {
        if (context.Request.ContentLength > maxBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return Task.CompletedTask;
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = maxBytes;
        }

        return next(context);
    }
}
