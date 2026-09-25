using Microsoft.Extensions.Options;
using TeamsWork.Ticketing.Mcp.Configuration;

namespace TeamsWork.Ticketing.Mcp.Auth;

/// <summary>
/// Resolves the acting user from the validated bearer token on the current HTTP request. Registered as scoped
/// so the result is computed once per request. Only used by the HTTP transport.
/// </summary>
public sealed class HttpActingUserProvider : IActingUserProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IOptions<TicketingOptions> _ticketingOptions;
    private ActingUser? _cached;

    public HttpActingUserProvider(IHttpContextAccessor httpContextAccessor, IOptions<TicketingOptions> ticketingOptions)
    {
        _httpContextAccessor = httpContextAccessor;
        _ticketingOptions = ticketingOptions;
    }

    public ValueTask<ActingUser> GetActingUserAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return ValueTask.FromResult(_cached);
        }

        HttpContext httpContext = _httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException("No HTTP context is available to resolve the acting user.");

        _cached = ClaimsActingUserResolver.Resolve(httpContext.User, _ticketingOptions.Value.ServiceAccount);
        return ValueTask.FromResult(_cached);
    }
}
