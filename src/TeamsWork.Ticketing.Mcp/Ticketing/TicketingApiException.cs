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

    private readonly bool _mayHaveBeenCarriedOut;

    /// <summary>
    /// True when a write failed after it may have reached the API (or after an earlier attempt of it may have), so the
    /// change may have been made. <see cref="OutcomeUnknown"/> says, besides, that repeating it isn't safe.
    /// </summary>
    public bool MayHaveBeenCarriedOut { get => _mayHaveBeenCarriedOut || OutcomeUnknown; init => _mayHaveBeenCarriedOut = value; }

    /// <summary>True when this server held the request back to keep within the API's rate limit: it was never sent.</summary>
    public bool Throttled { get; init; }

    /// <summary>
    /// Refused for rate, here or by the API (429): worth retrying after a wait, and nothing was changed. Never so when
    /// the write may have been carried out, as when an earlier attempt of it may have been.
    /// </summary>
    public bool IsRateLimited => !MayHaveBeenCarriedOut && (Throttled || StatusCode == HttpStatusCode.TooManyRequests);

    internal const string OutcomeUnknownAdvice =
        " The request may have been carried out anyway, so check before trying again (get_ticket_brief shows a ticket's latest " +
        "comments and changes; find_similar_tickets finds a ticket that may have just been created): repeating it could do it twice.";

    internal const string PossiblyAppliedAdvice =
        " The change may have been made anyway. Repeating it is safe, but check the ticket first (get_ticket_brief shows its " +
        "latest changes): it may already be done.";

    /// <summary>A failure of a create request whose outcome is unknown, with advice not to repeat it blindly.</summary>
    internal static TicketingApiException Unknown(HttpStatusCode? statusCode, string message, Exception? innerException = null) =>
        innerException is null
            ? new(statusCode, message + OutcomeUnknownAdvice) { OutcomeUnknown = true }
            : new(statusCode, message + OutcomeUnknownAdvice, innerException) { OutcomeUnknown = true };

    /// <summary>A failure of a write that is safe to repeat but may have been made, with advice to check first.</summary>
    internal static TicketingApiException PossiblyApplied(HttpStatusCode? statusCode, string message, Exception? innerException = null) =>
        innerException is null
            ? new(statusCode, message + PossiblyAppliedAdvice) { MayHaveBeenCarriedOut = true }
            : new(statusCode, message + PossiblyAppliedAdvice, innerException) { MayHaveBeenCarriedOut = true };
}
