using System.Globalization;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Ticketing;

/// <summary>
/// What a ticket list with date filters sends: the filter dates and the one <c>timezone</c> value they share.
/// <para>
/// How the live API treats them (checked on 2026-09-27): a filter is honoured only as a plain YYYY-MM-DD date (with a
/// time of day it is ignored and every ticket comes back); its boundary is the date's 00:00 UTC plus <c>timezone</c>
/// hours, the opposite way to the spec's "7 means GMT+7"; "after" matches values at or after the boundary and
/// "before" values at or before it. Nothing else in a ticket list depends on <c>timezone</c>.
/// </para>
/// <para>
/// Created and updated times are instants, so their days are the caller's local days: the caller's offset (on the
/// filtered day, for daylight saving) is sent negated, which puts each boundary at local midnight.
/// </para>
/// <para>
/// An expected date is a calendar date, but the API stores it as midnight in the offset of whoever set it (UTC for
/// one set at 0, 05:00 UTC for one set at -5), which isn't known when filtering. So a due date is matched to its day in
/// the instance's time zone (Ticketing:DefaultTimeZoneId), with each boundary at noon of the day before: a due date set
/// from anywhere within 12 hours of that zone, which covers the organisation's own users, this server, and UTC, falls
/// on its own day. When only expected-date filters are given, <c>timezone</c> is chosen to put the boundary exactly
/// there. Alongside created or updated filters it is fixed by those, so the boundaries fall on the caller's local
/// midnights and the date sent is the one whose boundary is nearest: due dates set in the instance's zone still fall on
/// their own day, but one set from elsewhere can sit on a boundary and land a day out (seen live for a date set at UTC
/// with the instance at UTC-5: 2 of 20 caller offsets).
/// </para>
/// </summary>
internal static class TicketDateFilters
{
    /// <summary>The <c>timezone</c> value to send, and the filter parameters (null when the query has no date filter).</summary>
    public sealed record Plan(int Timezone, IReadOnlyList<KeyValuePair<string, string?>> Parameters);

    public static Plan? For(TicketListQuery q, TimeZoneOffsetResolver zones)
    {
        var instants = new List<(string Name, DateOnly Day)>();
        Add(instants, "createdAfter", q.CreatedAfter);
        Add(instants, "createdBefore", q.CreatedBefore);
        Add(instants, "lastUpdateAfter", q.LastUpdateAfter);
        Add(instants, "lastUpdateBefore", q.LastUpdateBefore);

        var expected = new List<(string Name, DateOnly Day)>();
        Add(expected, "expectedDateAfter", q.ExpectedDateAfter);
        Add(expected, "expectedDateBefore", q.ExpectedDateBefore);

        if (instants.Count == 0 && expected.Count == 0)
        {
            return null;
        }

        int timezone;
        if (instants.Count > 0)
        {
            // One offset serves every boundary, so a range across a daylight-saving change is an hour off at one end.
            timezone = -zones.ResolveOn(q.TimezoneOffset, instants.Min(f => f.Day));
        }
        else
        {
            // Free to choose: put the first expected-date boundary exactly at noon of the day before, in the
            // instance's zone, with the date sent being that day or the one before, whichever keeps it within -12..+12.
            (_, DateOnly first) = expected[0];
            int wanted = NoonBefore(first, zones);
            timezone = wanted >= -12 ? wanted : wanted + 24;
        }

        var parameters = new List<KeyValuePair<string, string?>>();
        foreach ((string name, DateOnly day) in instants)
        {
            parameters.Add(new(name, Format(day)));
        }

        foreach ((string name, DateOnly day) in expected)
        {
            parameters.Add(new(name, Format(ExpectedDateToSend(day, timezone, zones, after: name == "expectedDateAfter"))));
        }

        return new Plan(timezone, parameters);
    }

    /// <summary>
    /// The date to send for an expected-date filter on <paramref name="day"/>, given the <c>timezone</c> sent: the one
    /// whose boundary (its 00:00 UTC plus that many hours) is nearest noon of the day before, in the instance's zone.
    /// Two can be equally near (12 hours either side), which is the usual case when created or updated filters fix
    /// <c>timezone</c> to a caller in the instance's zone. Then the boundaries are the zone's midnights, where the API
    /// stores due dates set in it, and "after" takes the later one and "before" the earlier: "after D" at D's midnight
    /// (at-or-after includes a date due on D), "before D" at D-1's (at-or-before includes D-1, not D).
    /// </summary>
    internal static DateOnly ExpectedDateToSend(DateOnly day, int timezone, TimeZoneOffsetResolver zones, bool after)
    {
        // The boundary for the date sent is day 00:00 UTC + (days * 24 + timezone) hours; the target is NoonBefore hours.
        int hours = NoonBefore(day, zones) - timezone;
        int days = hours % 24 == 12 || hours % 24 == -12
            ? (int)(after ? Math.Ceiling(hours / 24.0) : Math.Floor(hours / 24.0))
            : (int)Math.Round(hours / 24.0);
        return day.AddDays(days);
    }

    /// <summary>Hours from <paramref name="day"/>'s 00:00 UTC to noon of the day before in the instance's zone.</summary>
    private static int NoonBefore(DateOnly day, TimeZoneOffsetResolver zones) =>
        -12 - zones.DefaultOffsetAt(day.AddDays(-1), new TimeOnly(12, 0));

    private static void Add(List<(string, DateOnly)> filters, string name, DateOnly? day)
    {
        if (day is DateOnly d)
        {
            filters.Add((name, d));
        }
    }

    private static string Format(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
