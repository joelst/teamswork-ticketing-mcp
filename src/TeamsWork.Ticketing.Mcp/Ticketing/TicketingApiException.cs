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

    /// <summary>
    /// True when a non-idempotent request (a POST that creates something) failed after it may have reached the API:
    /// a timeout, a dropped connection, a 5xx, or a success response without the created item. The API may have
    /// carried it out, so repeating it could create a duplicate. The message says so too.
    /// </summary>
    public bool OutcomeUnknown { get; init; }

    internal const string OutcomeUnknownAdvice =
        " The request may have been carried out anyway, so check before trying again (for example with find_similar_tickets " +
        "or list_ticket_activities): repeating it could create a duplicate.";

    /// <summary>A failure of a create request whose outcome is unknown, with advice not to repeat it blindly.</summary>
    internal static TicketingApiException Unknown(HttpStatusCode? statusCode, string message, Exception? innerException = null) =>
        innerException is null
            ? new(statusCode, message + OutcomeUnknownAdvice) { OutcomeUnknown = true }
            : new(statusCode, message + OutcomeUnknownAdvice, innerException) { OutcomeUnknown = true };
}
