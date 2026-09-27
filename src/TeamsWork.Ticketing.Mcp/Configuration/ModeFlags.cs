namespace TeamsWork.Ticketing.Mcp.Configuration;

/// <summary>
/// The bare flags that choose how the server runs. They are read from the raw arguments and kept out of the host's
/// configuration: the command-line provider reads <c>--flag</c> without <c>=</c> as a key whose value is the next
/// argument, so <c>--stdio --Ticketing:DefaultTimeZoneId=...</c> would otherwise store the setting as the value of a
/// <c>stdio</c> key and never apply it.
/// </summary>
public static class ModeFlags
{
    public const string Stdio = "--stdio";

    public static bool IsModeFlag(string arg) =>
        string.Equals(arg, Stdio, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, AuthModeResolver.CommandLineFlag, StringComparison.OrdinalIgnoreCase);

    /// <summary>The arguments without the mode flags, for the host's configuration.</summary>
    public static string[] SettingsArguments(string[] args) => args.Where(a => !IsModeFlag(a)).ToArray();
}
