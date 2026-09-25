using System.Collections;
using System.Text;
using Microsoft.Extensions.Options;

namespace TeamsWork.Ticketing.Mcp.Configuration;

/// <summary>
/// Turns configuration failures at startup into a short message that says what is missing and how to set it,
/// instead of an unhandled-exception stack trace. Only setting names are ever printed, never their values.
/// </summary>
public static class StartupErrorReport
{
    public const string SetupGuideUrl = "https://github.com/joelst/teamswork-ticketing-mcp#configure-without-the-net-sdk";

    // Settings people commonly set by environment variable, used to spot names .NET will not bind.
    private static readonly string[] KnownSettings =
    [
        "Ticketing:ApiKey",
        "Ticketing:ServiceAccount:Id",
        "Ticketing:ServiceAccount:Name",
        "Ticketing:ServiceAccount:Email",
        "Ticketing:BaseUrl",
        "Ticketing:DefaultTimeZoneId",
        "KeyVault:Uri",
        "Entra:TenantId",
        "Entra:ClientId",
        "Auth:Mode",
        "Local:Port",
    ];

    /// <summary>
    /// Formats <paramref name="exception"/> if it is a configuration failure. Returns false for anything else, which
    /// should be left to crash normally.
    /// </summary>
    public static bool TryFormat(Exception exception, IDictionary environment, string? userSecretsPath, out string message)
    {
        List<string> problems = CollectProblems(exception);
        if (problems.Count == 0)
        {
            message = string.Empty;
            return false;
        }

        var text = new StringBuilder();
        text.AppendLine("The TeamsWork Ticketing MCP server cannot start because its configuration is incomplete:");
        text.AppendLine();
        foreach (string problem in problems)
        {
            text.Append("  - ").AppendLine(problem);
        }

        List<(string Found, string Expected)> misnamed = FindMisnamedVariables(environment);
        if (misnamed.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("These environment variables look like settings but are ignored, because the name needs a double");
            text.AppendLine("underscore wherever the setting has ':'. Rename them:");
            foreach ((string found, string expected) in misnamed)
            {
                text.Append("  ").Append(found).Append("  ->  ").AppendLine(expected);
            }
        }

        text.AppendLine();
        text.AppendLine(string.IsNullOrEmpty(userSecretsPath) ? "Set values as:" : "Set values in either of these places:");
        text.AppendLine("  - Environment variables, with '__' in place of ':' (for example Ticketing__ApiKey). A changed variable");
        text.AppendLine("    only reaches programs started afterwards, so restart your terminal or MCP client.");
        if (!string.IsNullOrEmpty(userSecretsPath))
        {
            text.AppendLine("  - The user-secrets file, as JSON with ':' names (for example \"Ticketing:ApiKey\"):");
            text.Append("    ").AppendLine(userSecretsPath);
        }

        text.AppendLine();
        text.Append("Setup guide: ").Append(SetupGuideUrl);
        message = text.ToString();
        return true;
    }

    /// <summary>
    /// Environment variables whose name matches a known setting once separators are ignored but lacks the
    /// <c>__</c> separator, such as <c>TICKETING_APIKEY</c> or <c>TICKETING_API_KEY</c> for <c>Ticketing__ApiKey</c>.
    /// </summary>
    public static List<(string Found, string Expected)> FindMisnamedVariables(IDictionary environment)
    {
        Dictionary<string, string> known = KnownSettings.ToDictionary(Normalize, k => k.Replace(":", "__", StringComparison.Ordinal), StringComparer.Ordinal);
        var result = new List<(string, string)>();

        foreach (string name in environment.Keys.OfType<string>().Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!name.Contains("__", StringComparison.Ordinal) && known.TryGetValue(Normalize(name), out string? expected))
            {
                result.Add((name, expected));
            }
        }

        return result;
    }

    private static string Normalize(string name) =>
        name.Replace("_", string.Empty, StringComparison.Ordinal).Replace(":", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    /// <summary>Configuration problems in <paramref name="exception"/>; empty if it contains anything else.</summary>
    private static List<string> CollectProblems(Exception exception)
    {
        var problems = new List<string>();
        return Collect(exception, problems) ? problems : [];

        static bool Collect(Exception e, List<string> into)
        {
            switch (e)
            {
                case OptionsValidationException ove:
                    into.AddRange(ove.Failures);
                    return true;
                case StartupConfigurationException sce:
                    into.Add(sce.Message);
                    return true;
                case AggregateException ae:
                    return ae.InnerExceptions.All(inner => Collect(inner, into));
                default:
                    return false;
            }
        }
    }
}
