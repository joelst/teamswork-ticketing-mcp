namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// The descriptions agents see for the date filters, shared by every tool that has them so they say the same thing.
/// Each is one line, since every tool's schema is sent with every conversation; the caveats that apply to all of them
/// (whole days, one offset per request, midnight cuts) are in the server instructions once (Caveats). See
/// TicketDateFilters for how each is sent.
/// </summary>
internal static class DateFilterText
{
    public const string CreatedAfter = "Created on or after this day (YYYY-MM-DD).";
    public const string CreatedBefore = "Created before this day (YYYY-MM-DD). For one day: createdAfter=D, createdBefore=D+1.";
    public const string LastUpdateAfter = "Last updated on or after this day (YYYY-MM-DD).";
    public const string LastUpdateBefore = "Last updated before this day (YYYY-MM-DD).";
    public const string ExpectedDateAfter = "Due on or after this date (YYYY-MM-DD).";
    public const string ExpectedDateBefore = "Due before this date (YYYY-MM-DD).";

    public const string TimezoneOffset = "Your UTC offset in whole hours (default: the server's zone).";

    /// <summary>What every date filter has in common, for the server instructions.</summary>
    public const string Caveats =
        "Date filters take whole days (YYYY-MM-DD; a time of day is refused). Created and updated days are local to " +
        "timezoneOffset (default: the server's zone); a 'before' day is excluded, though a time of exactly that midnight is " +
        "included, and all filters in one call share one offset, so across a daylight-saving change the later end can be an " +
        "hour off. Due dates are calendar dates in the server's zone. Returned times are UTC.";
}
