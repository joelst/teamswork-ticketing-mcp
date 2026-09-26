namespace TeamsWork.Ticketing.Mcp.Auth;

/// <summary>
/// Raised when the caller's identity can't be turned into an account to attribute ticket changes to. The message is
/// written for the agent (it says what to fix in the token or configuration), so tools pass it on.
/// </summary>
public sealed class ActingUserException : InvalidOperationException
{
    public ActingUserException(string message)
        : base(message)
    {
    }

    public ActingUserException()
    {
    }

    public ActingUserException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
