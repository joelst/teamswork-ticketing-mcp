using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Ticketing;

/// <summary>
/// Caches instance settings and tag categories, which change rarely but are read by every name lookup and by
/// get_instance, a large response. Shared by all callers: every caller uses the same API key, so they see the same
/// instance. The client is passed in on each call rather than held, because the typed <see cref="TicketingClient"/>
/// is transient and holding one would pin its HTTP handler for the life of the process.
/// </summary>
public sealed class InstanceCache
{
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;
    private readonly TimeZoneOffsetResolver _timeZones;

    // Instance responses carry times in the requested offset, so each offset is cached separately.
    private readonly ConcurrentDictionary<int, Entry<Instance>> _instances = new();
    private Entry<IReadOnlyList<TagCategory>>? _tags;

    public InstanceCache(IOptions<TicketingOptions> options, TimeZoneOffsetResolver timeZones, TimeProvider? time = null)
    {
        _ttl = TimeSpan.FromSeconds(options.Value.InstanceCacheSeconds);
        _timeZones = timeZones;
        _time = time ?? TimeProvider.System;
    }

    public async Task<Instance> GetInstanceAsync(TicketingClient client, int? timezoneOffset, bool refresh, CancellationToken cancellationToken)
    {
        int offset = _timeZones.Resolve(timezoneOffset);
        if (!refresh && _instances.TryGetValue(offset, out Entry<Instance>? cached) && cached.Expires > _time.GetUtcNow())
        {
            return cached.Value;
        }

        Instance instance = await client.GetInstanceAsync(offset, cancellationToken);
        if (_ttl > TimeSpan.Zero)
        {
            _instances[offset] = new Entry<Instance>(instance, _time.GetUtcNow() + _ttl);
        }

        return instance;
    }

    /// <summary>Tag categories that haven't been deleted.</summary>
    public async Task<IReadOnlyList<TagCategory>> GetTagCategoriesAsync(TicketingClient client, bool refresh, CancellationToken cancellationToken)
    {
        Entry<IReadOnlyList<TagCategory>>? cached = _tags;
        if (!refresh && cached is not null && cached.Expires > _time.GetUtcNow())
        {
            return cached.Value;
        }

        ListResponse<TagCategory> r = await client.ListTagCategoriesAsync(cancellationToken);
        List<TagCategory> categories = (r.Items ?? []).Where(c => c.Deleted != true).ToList();
        if (_ttl > TimeSpan.Zero)
        {
            _tags = new Entry<IReadOnlyList<TagCategory>>(categories, _time.GetUtcNow() + _ttl);
        }

        return categories;
    }

    private sealed record Entry<T>(T Value, DateTimeOffset Expires);
}
