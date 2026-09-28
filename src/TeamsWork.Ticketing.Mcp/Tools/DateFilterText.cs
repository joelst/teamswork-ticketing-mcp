namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// The descriptions agents see for the date filters, shared by every tool that has them so they say the same thing.
/// See TicketDateFilters for how each is sent.
/// </summary>
internal static class DateFilterText
{
    private const string WholeDays = " The API filters by whole days, so a time of day is refused; returned times are UTC.";

    // Every date filter in a request shares one offset (see TicketDateFilters), which the two caveats below follow from.
    private const string OneOffset =
        " Both ends of a range use one offset: without timezoneOffset, the server zone's on the earlier day, so across a " +
        "daylight-saving change the later end is an hour off.";

    // The API's "before" is at or before its cut, which for these is the day's local midnight.
    private const string AtOrBefore = " The API cuts at or before that local midnight, so a time of exactly midnight is included too.";

    private const string Mixed =
        " On its own it ignores timezoneOffset; alongside a created or updated filter, a due date set from another time zone " +
        "can come out a day off. A time of day is refused.";

    public const string CreatedAfter = "Only tickets created on or after this day (YYYY-MM-DD, your local day at timezoneOffset)." + WholeDays;
    public const string CreatedBefore = "Only tickets created before this day starts (YYYY-MM-DD, your local day at timezoneOffset)." + AtOrBefore + " For one day, give it as createdAfter and the next day here." + OneOffset + WholeDays;
    public const string LastUpdateAfter = "Only tickets last updated on or after this day (YYYY-MM-DD, your local day at timezoneOffset)." + WholeDays;
    public const string LastUpdateBefore = "Only tickets last updated before this day starts (YYYY-MM-DD, your local day at timezoneOffset)." + AtOrBefore + OneOffset + WholeDays;
    public const string ExpectedDateAfter = "Only tickets due on or after this date (YYYY-MM-DD, a calendar date in the server's time zone)." + Mixed;
    public const string ExpectedDateBefore = "Only tickets due before this date (YYYY-MM-DD, a calendar date in the server's time zone; the date itself isn't included)." + Mixed;
}
