using System.Security.Claims;
using TeamsWork.Ticketing.Mcp.Configuration;

namespace TeamsWork.Ticketing.Mcp.Auth;

/// <summary>
/// Turns an Entra ID <see cref="ClaimsPrincipal"/> into an <see cref="ActingUser"/>. Handles both the short
/// claim names Entra v2 tokens use and the long WS-Federation names produced when inbound claim mapping is on.
/// </summary>
public static class ClaimsActingUserResolver
{
    private static readonly string[] ObjectIdClaims =
    [
        "oid",
        "http://schemas.microsoft.com/identity/claims/objectidentifier",
    ];

    private static readonly string[] NameClaims =
    [
        "name",
        ClaimTypes.Name,
    ];

    private static readonly string[] EmailClaims =
    [
        "preferred_username",
        "email",
        ClaimTypes.Email,
        "upn",
        ClaimTypes.Upn,
    ];

    private static readonly string[] ScopeClaims =
    [
        "scp",
        "http://schemas.microsoft.com/identity/claims/scope",
    ];

    private static readonly string[] RoleClaims =
    [
        "roles",
        ClaimTypes.Role,
    ];

    /// <summary>True when the token was issued for a signed-in user (delegated permissions).</summary>
    public static bool IsDelegatedToken(ClaimsPrincipal principal) =>
        FindFirst(principal, ScopeClaims) is not null;

    /// <summary>True when the token was issued to an application with no user (client credentials / managed identity).</summary>
    public static bool IsAppOnlyToken(ClaimsPrincipal principal) =>
        !IsDelegatedToken(principal) && FindFirst(principal, RoleClaims) is not null;

    public static bool HasScope(ClaimsPrincipal principal, string scope)
    {
        foreach (Claim c in principal.FindAll(c => ScopeClaims.Contains(c.Type, StringComparer.Ordinal)))
        {
            if (c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasAppRole(ClaimsPrincipal principal, string role) =>
        principal.FindAll(c => RoleClaims.Contains(c.Type, StringComparer.Ordinal))
            .Any(c => string.Equals(c.Value, role, StringComparison.Ordinal));

    /// <summary>
    /// Resolves the acting user. Delegated tokens yield the signed-in user; app-only tokens yield the configured
    /// service account. Throws <see cref="InvalidOperationException"/> when neither is possible.
    /// </summary>
    public static ActingUser Resolve(ClaimsPrincipal principal, ServiceAccountOptions? serviceAccount)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            throw new InvalidOperationException("The request is not authenticated.");
        }

        if (IsDelegatedToken(principal))
        {
            string? id = FindFirst(principal, ObjectIdClaims);
            string? email = FindFirst(principal, EmailClaims);
            string? name = FindFirst(principal, NameClaims) ?? email;

            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(
                    "The access token does not carry the user's object ID, name, and email. Ensure the server app registration " +
                    "requests the 'profile' and 'email' optional claims, or that the client requests the 'openid profile email' scopes.");
            }

            return new ActingUser(id, name, email, ActingUserSource.DelegatedToken);
        }

        if (serviceAccount?.IsConfigured == true)
        {
            return new ActingUser(serviceAccount.Id!, serviceAccount.Name!, serviceAccount.Email!, ActingUserSource.ServiceAccount);
        }

        throw new InvalidOperationException(
            "The caller is an application (no user identity) and no Ticketing:ServiceAccount is configured, so ticket " +
            "changes cannot be attributed. Configure a service account or call with a user (delegated) token.");
    }

    private static string? FindFirst(ClaimsPrincipal principal, string[] claimTypes)
    {
        foreach (string type in claimTypes)
        {
            string? value = principal.FindFirst(type)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
