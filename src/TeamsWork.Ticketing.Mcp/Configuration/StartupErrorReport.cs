using System.Collections;
using System.Reflection;
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

    // Every setting the server reads, used to spot environment variables .NET will not bind. The options classes are
    // walked so a new property is covered automatically; the settings read directly from configuration are listed.
    internal static readonly string[] KnownSettings =
    [
        .. SettingPaths(TicketingOptions.SectionName, typeof(TicketingOptions)),
        .. SettingPaths(EntraOptions.SectionName, typeof(EntraOptions)),
        AuthModeResolver.ConfigurationKey,
        "Local:Port",
        "KeyVault:Uri",
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
        text.AppendLine("The TeamsWork Ticketing MCP server cannot start until these settings are fixed:");
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
    /// Environment variables that match a known setting once separators are ignored, but that the environment
    /// provider would bind to some other key. Examples: <c>TICKETING_APIKEY</c> or <c>TICKETING_API_KEY</c> for
    /// <c>Ticketing__ApiKey</c>, and <c>Ticketing__ServiceAccount_Email</c>, which binds to
    /// <c>Ticketing:ServiceAccount_Email</c>.
    /// </summary>
    public static List<(string Found, string Expected)> FindMisnamedVariables(IDictionary environment)
    {
        Dictionary<string, string> known = KnownSettings.ToDictionary(Normalize, k => k.Replace(":", "__", StringComparison.Ordinal), StringComparer.Ordinal);
        var result = new List<(string, string)>();

        foreach (string name in environment.Keys.OfType<string>().Order(StringComparer.OrdinalIgnoreCase))
        {
            // The environment provider turns "__" into ':' and matches keys case-insensitively.
            string boundKey = name.Replace("__", ":", StringComparison.Ordinal);
            bool bindsCorrectly = KnownSettings.Contains(boundKey, StringComparer.OrdinalIgnoreCase);

            if (!bindsCorrectly && known.TryGetValue(Normalize(name), out string? expected))
            {
                result.Add((name, expected));
            }
        }

        return result;
    }

    /// <summary>Configuration paths of the settable properties of <paramref name="type"/>, nested classes included.</summary>
    private static IEnumerable<string> SettingPaths(string prefix, Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite)
            .SelectMany(p =>
            {
                Type t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                string path = $"{prefix}:{p.Name}";
                return t.IsPrimitive || t.IsEnum || t == typeof(string) ? [path] : SettingPaths(path, t);
            });

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
