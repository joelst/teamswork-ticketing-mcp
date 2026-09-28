using Microsoft.Extensions.Options;
using TeamsWork.Ticketing.Mcp.Configuration;

namespace TeamsWork.Ticketing.Mcp.Ticketing;

/// <summary>
/// Computes a caller's UTC offset in whole hours (the API's <c>timezone</c> takes integers), from an explicit value or
/// the configured IANA time zone, so daylight saving is handled. This is the spec's convention (local = UTC + offset),
/// which the live API follows for instance times (SLA working hours) and for writes (an expected date set at -5 is
/// stored as its midnight at UTC-5). Ticket-list date filters apply the offset the other way; see
/// <see cref="TicketDateFilters"/>.
/// </summary>
public sealed class TimeZoneOffsetResolver
{
    private readonly TimeZoneInfo _timeZone;
    private readonly TimeProvider _timeProvider;

    public TimeZoneOffsetResolver(IOptions<TicketingOptions> options, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        string id = options.Value.DefaultTimeZoneId;
        try
        {
            _timeZone = TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException ex)
        {
            throw new InvalidOperationException($"Ticketing:DefaultTimeZoneId '{id}' is not a known time zone.", ex);
        }
        catch (InvalidTimeZoneException ex)
        {
            throw new InvalidOperationException($"Ticketing:DefaultTimeZoneId '{id}' is not a valid time zone.", ex);
        }
    }

    public string TimeZoneId => _timeZone.Id;

    /// <summary>Returns the explicit override when supplied, otherwise the current offset of the default zone.</summary>
    public int Resolve(int? explicitOffsetHours)
    {
        if (explicitOffsetHours is int o)
        {
            if (o is < -12 or > 14)
            {
                throw new TicketingApiException("timezoneOffset must be between -12 and 14 hours.");
            }

            return o;
        }

        return Hours(_timeZone.GetUtcOffset(_timeProvider.GetUtcNow()));
    }

    /// <summary>
    /// As <see cref="Resolve"/>, but for a time on <paramref name="day"/> rather than now: the default zone's offset at
    /// that day's local midnight, so a filter on a winter day asked for in summer uses the winter offset.
    /// </summary>
    public int ResolveOn(int? explicitOffsetHours, DateOnly day) =>
        explicitOffsetHours is not null ? Resolve(explicitOffsetHours) : DefaultOffsetAt(day, TimeOnly.MinValue);

    /// <summary>The configured zone's offset at <paramref name="time"/> on <paramref name="day"/>, local time.</summary>
    public int DefaultOffsetAt(DateOnly day, TimeOnly time) =>
        Hours(_timeZone.GetUtcOffset(day.ToDateTime(time, DateTimeKind.Unspecified)));

    // Half-hour zones (India, parts of Australia) are rounded: the API takes whole hours.
    private static int Hours(TimeSpan offset) => (int)Math.Round(offset.TotalHours, MidpointRounding.AwayFromZero);
}
