using Microsoft.Extensions.Options;
using TeamsWork.Ticketing.Mcp.Configuration;

namespace TeamsWork.Ticketing.Mcp.Ticketing;

/// <summary>
/// Computes the integer UTC offset (in hours) that the Ticketing API expects in its <c>timezone</c> parameter,
/// using the configured IANA time zone so daylight-saving changes are handled automatically.
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

        TimeSpan offset = _timeZone.GetUtcOffset(_timeProvider.GetUtcNow());
        return (int)Math.Round(offset.TotalHours, MidpointRounding.AwayFromZero);
    }
}
