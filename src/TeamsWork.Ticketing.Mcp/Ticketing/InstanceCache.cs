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
public sealed class InstanceCache : IDisposable
{
    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;
    private readonly TimeZoneOffsetResolver _timeZones;

    // Instance responses carry times in the requested offset, so each offset is cached separately.
    private readonly ConcurrentDictionary<int, Entry<Instance>> _instances = new();
    private Entry<IReadOnlyList<TagCategory>>? _tags;

    // One fetch at a time, so callers that miss together share one upstream request instead of each making one.
    private readonly SemaphoreSlim _instanceGate = new(1, 1);
    private readonly SemaphoreSlim _tagGate = new(1, 1);

    public InstanceCache(IOptions<TicketingOptions> options, TimeZoneOffsetResolver timeZones, TimeProvider? time = null)
    {
        _ttl = TimeSpan.FromSeconds(options.Value.InstanceCacheSeconds);
        _timeZones = timeZones;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// The instance settings. <paramref name="refresh"/> drops every cached copy, whatever its offset, so later name
    /// lookups (which may use another offset) see the change too.
    /// </summary>
    public async Task<Instance> GetInstanceAsync(TicketingClient client, int? timezoneOffset, bool refresh, CancellationToken cancellationToken)
    {
        int offset = _timeZones.Resolve(timezoneOffset);
        if (!refresh && Fresh(_instances.GetValueOrDefault(offset)) is Instance cached)
        {
            return cached;
        }

        await _instanceGate.WaitAsync(cancellationToken);
        try
        {
            if (refresh)
            {
                _instances.Clear();
            }
            else if (Fresh(_instances.GetValueOrDefault(offset)) is Instance fetchedMeanwhile)
            {
                return fetchedMeanwhile;
            }

            Instance instance = await client.GetInstanceAsync(offset, cancellationToken);
            if (_ttl > TimeSpan.Zero)
            {
                _instances[offset] = new Entry<Instance>(instance, _time.GetUtcNow() + _ttl);
            }

            return instance;
        }
        finally
        {
            _instanceGate.Release();
        }
    }

    /// <summary>Tag categories that haven't been deleted.</summary>
    public async Task<IReadOnlyList<TagCategory>> GetTagCategoriesAsync(TicketingClient client, bool refresh, CancellationToken cancellationToken)
    {
        if (!refresh && Fresh(_tags) is IReadOnlyList<TagCategory> cached)
        {
            return cached;
        }

        await _tagGate.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && Fresh(_tags) is IReadOnlyList<TagCategory> fetchedMeanwhile)
            {
                return fetchedMeanwhile;
            }

            ListResponse<TagCategory> r = await client.ListTagCategoriesAsync(cancellationToken);
            List<TagCategory> categories = (r.Items ?? []).Where(c => c.Deleted != true).ToList();
            _tags = _ttl > TimeSpan.Zero ? new Entry<IReadOnlyList<TagCategory>>(categories, _time.GetUtcNow() + _ttl) : null;
            return categories;
        }
        finally
        {
            _tagGate.Release();
        }
    }

    public void Dispose()
    {
        _instanceGate.Dispose();
        _tagGate.Dispose();
    }

    private T? Fresh<T>(Entry<T>? entry)
        where T : class =>
        entry is not null && entry.Expires > _time.GetUtcNow() ? entry.Value : null;

    private sealed record Entry<T>(T Value, DateTimeOffset Expires);
}
