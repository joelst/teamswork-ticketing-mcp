using System.Text.RegularExpressions;

namespace TeamsWork.Ticketing.Mcp.Configuration;

/// <summary>
/// A setting is missing or invalid, so the server cannot start. The message says what to change; the entry point
/// prints it without a stack trace (see <see cref="StartupErrorReport"/>).
/// </summary>
public sealed partial class StartupConfigurationException : InvalidOperationException
{
    public StartupConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Replaces the configuration binder's "Failed to convert configuration value ..." error. That message and its
    /// inner exceptions include the offending value, which could be a secret, so only the setting path and expected
    /// type are kept, and the original is deliberately not attached (the host logs inner exceptions).
    /// </summary>
    public static StartupConfigurationException FromConversionFailure(InvalidOperationException exception, string fallbackPath)
    {
        Match match = ConversionFailure().Match(exception.Message);
        string path = match.Success ? match.Groups["path"].Value : fallbackPath;
        Type? type = match.Success ? Type.GetType(match.Groups["type"].Value, throwOnError: false) : null;
        string expected = (type is null ? null : Nullable.GetUnderlyingType(type) ?? type) switch
        {
            Type t when t == typeof(int) || t == typeof(long) => "a whole number",
            Type t when t == typeof(bool) => "true or false",
            Type t when t == typeof(double) || t == typeof(decimal) => "a number",
            Type t when t.IsEnum => $"one of {string.Join(", ", Enum.GetNames(t))}",
            _ => "the expected type",
        };

        return new StartupConfigurationException($"{path} has a value that cannot be read as {expected}.");
    }

    /// <summary>Runs <paramref name="read"/>, reporting a value of the wrong type as a setting to fix.</summary>
    public static T ReadSetting<T>(Func<T> read, string path)
    {
        try
        {
            return read();
        }
        catch (InvalidOperationException ex) when (ex is not StartupConfigurationException)
        {
            throw FromConversionFailure(ex, path);
        }
    }

    [GeneratedRegex(@" at '(?<path>[^']+)' to type '(?<type>[^']+)'")]
    private static partial Regex ConversionFailure();
}
