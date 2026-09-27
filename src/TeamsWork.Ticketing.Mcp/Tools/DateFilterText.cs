namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// The descriptions agents see for the date filters, shared by every tool that has them so they say the same thing.
/// See TicketDateFilters for how each is sent.
/// </summary>
internal static class DateFilterText
{
    private const string WholeDays = " The API filters by whole days, so a time of day is refused; returned times are UTC.";

    public const string CreatedAfter = "Only tickets created on or after this day (YYYY-MM-DD, your local day at timezoneOffset)." + WholeDays;
    public const string CreatedBefore = "Only tickets created before this day starts (YYYY-MM-DD, your local day at timezoneOffset). For one day, give it as createdAfter and the next day here." + WholeDays;
    public const string LastUpdateAfter = "Only tickets last updated on or after this day (YYYY-MM-DD, your local day at timezoneOffset)." + WholeDays;
    public const string LastUpdateBefore = "Only tickets last updated before this day starts (YYYY-MM-DD, your local day at timezoneOffset)." + WholeDays;
    public const string ExpectedDateAfter = "Only tickets due on or after this date (YYYY-MM-DD, a calendar date: timezoneOffset doesn't change it). A time of day is refused.";
    public const string ExpectedDateBefore = "Only tickets due before this date (YYYY-MM-DD, a calendar date; the date itself isn't included). A time of day is refused.";
}
