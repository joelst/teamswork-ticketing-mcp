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
        ServiceAccountOptions? sa = options.Value.ServiceAccount;
        if (sa?.IsConfigured != true)
        {
            throw new ActingUserException(
                "Ticketing:ServiceAccount:Id, :Name and :Email are required when running without Entra authentication. " +
                "They identify who ticket changes are attributed to.");
        }

        // Used exactly as validated: trimmed, and an object ID in the standard form the help desk stores.
        string id = sa.Id!.Trim();
        _user = new ActingUser(
            Guid.TryParseExact(id, "D", out Guid objectId) ? objectId.ToString("D") : id,
            sa.Name!.Trim(),
            sa.Email!.Trim(),
            ActingUserSource.ServiceAccount);
    }

    public ValueTask<ActingUser> GetActingUserAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_user);
}
