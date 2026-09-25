using System.Security.Claims;
using TeamsWork.Ticketing.Mcp.Auth;
using TeamsWork.Ticketing.Mcp.Configuration;

namespace TeamsWork.Ticketing.Mcp.Tests;

public sealed class ClaimsActingUserResolverTests
{
    private static readonly ServiceAccountOptions ServiceAccount = new() { Id = "sa-id", Name = "Ticketing Bot", Email = "bot@example.test" };

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "Bearer"));

    [Fact]
    public void Delegated_token_yields_signed_in_user()
    {
        ClaimsPrincipal p = Principal(("scp", "access_as_user"), ("oid", "u-1"), ("name", "Pat Example"), ("preferred_username", "pat@example.test"));

        ActingUser user = ClaimsActingUserResolver.Resolve(p, ServiceAccount);

        Assert.Equal("u-1", user.Id);
        Assert.Equal("Pat Example", user.Name);
        Assert.Equal("pat@example.test", user.Email);
        Assert.Equal(ActingUserSource.DelegatedToken, user.Source);
    }

    [Fact]
    public void Delegated_token_with_long_claim_names_is_supported()
    {
        ClaimsPrincipal p = Principal(
            ("http://schemas.microsoft.com/identity/claims/scope", "access_as_user"),
            ("http://schemas.microsoft.com/identity/claims/objectidentifier", "u-2"),
            (ClaimTypes.Name, "Sam"),
            (ClaimTypes.Email, "sam@example.test"));

        ActingUser user = ClaimsActingUserResolver.Resolve(p, null);

        Assert.Equal("u-2", user.Id);
        Assert.Equal("sam@example.test", user.Email);
    }

    [Fact]
    public void Delegated_token_prefers_user_over_service_account()
    {
        ClaimsPrincipal p = Principal(("scp", "access_as_user"), ("oid", "u-3"), ("name", "Real Person"), ("email", "real@example.test"));

        ActingUser user = ClaimsActingUserResolver.Resolve(p, ServiceAccount);

        Assert.Equal("Real Person", user.Name);
    }

    [Fact]
    public void App_only_token_uses_service_account()
    {
        ClaimsPrincipal p = Principal(("roles", "Ticketing.ReadWrite"), ("oid", "sp-oid"));

        ActingUser user = ClaimsActingUserResolver.Resolve(p, ServiceAccount);

        Assert.Equal("Ticketing Bot", user.Name);
        Assert.Equal(ActingUserSource.ServiceAccount, user.Source);
    }

    [Fact]
    public void App_only_token_without_service_account_fails_clearly()
    {
        ClaimsPrincipal p = Principal(("roles", "Ticketing.ReadWrite"));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => ClaimsActingUserResolver.Resolve(p, null));

        Assert.Contains("ServiceAccount", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Delegated_token_missing_email_fails_clearly()
    {
        ClaimsPrincipal p = Principal(("scp", "access_as_user"), ("oid", "u-4"), ("name", "No Email"));

        Assert.Throws<InvalidOperationException>(() => ClaimsActingUserResolver.Resolve(p, ServiceAccount));
    }

    [Fact]
    public void Unauthenticated_principal_is_rejected()
    {
        var p = new ClaimsPrincipal(new ClaimsIdentity());

        Assert.Throws<InvalidOperationException>(() => ClaimsActingUserResolver.Resolve(p, ServiceAccount));
    }

    [Fact]
    public void HasScope_handles_space_separated_scopes()
    {
        ClaimsPrincipal p = Principal(("scp", "profile access_as_user email"));

        Assert.True(ClaimsActingUserResolver.HasScope(p, "access_as_user"));
        Assert.False(ClaimsActingUserResolver.HasScope(p, "access_as_admin"));
    }

    [Fact]
    public void HasAppRole_is_exact_match()
    {
        ClaimsPrincipal p = Principal(("roles", "Ticketing.ReadWrite"));

        Assert.True(ClaimsActingUserResolver.HasAppRole(p, "Ticketing.ReadWrite"));
        Assert.False(ClaimsActingUserResolver.HasAppRole(p, "Ticketing.Read"));
    }
}
