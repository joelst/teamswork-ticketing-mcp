namespace TeamsWork.Ticketing.Mcp.Configuration;

/// <summary>How callers of the HTTP transport are authenticated.</summary>
public enum AuthMode
{
    /// <summary>Microsoft Entra ID bearer tokens are required. Used for every remote deployment.</summary>
    Entra,

    /// <summary>
    /// No authentication. The server binds to 127.0.0.1 only, rejects non-loopback connections, and attributes
    /// every write to the configured <c>Ticketing:ServiceAccount</c>, which is mandatory in this mode.
    /// Enabled with <c>--local</c> or <c>Auth:Mode=Local</c>. Refused inside Azure Container Apps.
    /// </summary>
    Local,
}

public static class AuthModeResolver
{
    public const string ConfigurationKey = "Auth:Mode";
    public const string CommandLineFlag = "--local";

    public static AuthMode Resolve(IConfiguration configuration, string[] args)
    {
        if (args.Contains(CommandLineFlag, StringComparer.OrdinalIgnoreCase))
        {
            return AuthMode.Local;
        }

        string? configured = configuration[ConfigurationKey]?.Trim();
        if (string.IsNullOrEmpty(configured))
        {
            return AuthMode.Entra;
        }

        // Names only: Enum.TryParse would also accept "1", and a typo must not quietly fall back to Entra and then
        // fail with an unrelated "Entra:TenantId ... must be configured".
        foreach (AuthMode mode in Enum.GetValues<AuthMode>())
        {
            if (string.Equals(configured, mode.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return mode;
            }
        }

        throw new StartupConfigurationException(
            $"{ConfigurationKey} must be {string.Join(" or ", Enum.GetNames<AuthMode>())}. Leave it unset for Entra, or start with {CommandLineFlag}.");
    }
}
