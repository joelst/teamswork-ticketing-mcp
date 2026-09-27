using Microsoft.Extensions.Options;
using TeamsWork.Ticketing.Mcp.Configuration;

namespace TeamsWork.Ticketing.Mcp.Auth;

/// <summary>
/// Acting user for the unauthenticated transports (stdio, and HTTP in local mode). There is no token to read an
/// identity from, so the operator must say who ticket changes are attributed to via
/// <c>Ticketing:ServiceAccount:{Id,Name,Email}</c>. Startup fails when that is missing.
/// </summary>
public sealed class ServiceAccountActingUserProvider : IActingUserProvider
{
    private readonly ActingUser _user;

    public ServiceAccountActingUserProvider(IOptions<TicketingOptions> options)
    {
        // Used exactly as validated, in the form every other person is compared and sent in.
        (string id, string name, string email) = options.Value.ServiceAccount?.Canonical() ?? throw new ActingUserException(
            "Ticketing:ServiceAccount:Id, :Name and :Email are required when running without Entra authentication, and must be a " +
            "valid identity. They identify who ticket changes are attributed to.");
        _user = new ActingUser(id, name, email, ActingUserSource.ServiceAccount);
    }

    public ValueTask<ActingUser> GetActingUserAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_user);
}
