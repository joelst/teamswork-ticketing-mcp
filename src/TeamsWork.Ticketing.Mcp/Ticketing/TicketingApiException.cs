using System.Net;

namespace TeamsWork.Ticketing.Mcp.Ticketing;

/// <summary>
/// Raised when the upstream Ticketing API returns an error. The message is safe to show to an agent: it never
/// contains the request URL (which carries the API key) or a stack trace.
/// </summary>
public sealed class TicketingApiException : Exception
{
    public TicketingApiException(HttpStatusCode? statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public TicketingApiException(HttpStatusCode? statusCode, string message, Exception innerException)
        : base(message, innerException)
    {
        StatusCode = statusCode;
    }

    public TicketingApiException(string message)
        : base(message)
    {
    }

    public TicketingApiException()
    {
    }

    public TicketingApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public HttpStatusCode? StatusCode { get; }
}
