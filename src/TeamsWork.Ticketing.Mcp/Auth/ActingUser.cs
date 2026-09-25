using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Auth;

/// <summary>The identity recorded as the actor on ticket writes. Never supplied by the MCP client.</summary>
public sealed record ActingUser(string Id, string Name, string Email, ActingUserSource Source)
{
    public TicketUser ToTicketUser() => new(Id, Name, Email);
}

public enum ActingUserSource
{
    /// <summary>Resolved from a delegated (user) Entra token.</summary>
    DelegatedToken,

    /// <summary>
    /// The configured service account: used for app-only tokens and for the unauthenticated transports
    /// (stdio, local HTTP).
    /// </summary>
    ServiceAccount,
}

/// <summary>Resolves the acting user for the current request or process.</summary>
public interface IActingUserProvider
{
    ValueTask<ActingUser> GetActingUserAsync(CancellationToken cancellationToken);
}
