namespace TeamsWork.Ticketing.Mcp.Configuration;

/// <summary>
/// A setting is missing or invalid, so the server cannot start. The message says what to change; the entry point
/// prints it without a stack trace (see <see cref="StartupErrorReport"/>).
/// </summary>
public sealed class StartupConfigurationException : InvalidOperationException
{
    public StartupConfigurationException(string message)
        : base(message)
    {
    }
}
