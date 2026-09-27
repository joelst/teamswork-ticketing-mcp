using Microsoft.Extensions.Options;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Ticketing;

/// <summary>
/// Caches instance settings and tag categories, which change rarely but are read by every name lookup and by
/// get_instance, a large response. Shared by all callers: every caller uses the same API key, so they see the same
/// instance. The client is passed in on each call rather than held, because the typed <see cref="TicketingClient"/>
/// is transient and holding one would pin its HTTP handler for the life of the process.
/// <para>
/// Because the cache is shared, no caller can empty it or make it re-read at will: a copy is replaced only once a
/// fresh one has been read, callers that need a read at the same time share one, and a refresh is honoured only when
/// the copy is older than <see cref="MinRefreshInterval"/>.
/// </para>
/// </summary>
public sealed class InstanceCache
{
    /// <summary>How recent a copy must be for a refresh to be answered from it instead of re-read.</summary>
    public static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;
    private readonly TimeZoneOffsetResolver _timeZones;
    private readonly object _lock = new();

    // Instance responses carry times in the requested offset, so each offset is cached separately.
    private readonly Dictionary<int, Entry<Instance>> _instances = [];
    private readonly Dictionary<int, Task<Instance>> _instanceReads = [];
    private Entry<IReadOnlyList<TagCategory>>? _tags;
    private Task<IReadOnlyList<TagCategory>>? _tagRead;

    public InstanceCache(IOptions<TicketingOptions> options, TimeZoneOffsetResolver timeZones, TimeProvider? time = null)
    {
        _ttl = TimeSpan.FromSeconds(options.Value.InstanceCacheSeconds);
        _timeZones = timeZones;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// The instance settings. <paramref name="refresh"/> re-reads them unless they were read in the last
    /// <see cref="MinRefreshInterval"/>, and a successful re-read replaces every offset's copy, so later name lookups
    /// (which may use another offset) see the change too.
    /// </summary>
    public Task<Instance> GetInstanceAsync(TicketingClient client, int? timezoneOffset, bool refresh, CancellationToken cancellationToken)
    {
        int offset = _timeZones.Resolve(timezoneOffset);
        TaskCompletionSource<Instance>? mine = null;
        Task<Instance> read;
        lock (_lock)
        {
            if (Usable(_instances.GetValueOrDefault(offset), refresh) is Instance cached)
            {
                return Task.FromResult(cached);
            }

            if (!_instanceReads.TryGetValue(offset, out read!))
            {
                mine = new TaskCompletionSource<Instance>(TaskCreationOptions.RunContinuationsAsynchronously);
                read = mine.Task;
                _instanceReads[offset] = read;
            }
        }

        if (mine is not null)
        {
            _ = ReadAsync(mine, () => client.GetInstanceAsync(offset, CancellationToken.None), instance =>
            {
                if (refresh)
                {
                    _instances.Clear();
                }

                if (_ttl > TimeSpan.Zero)
                {
                    _instances[offset] = NewEntry(instance);
                }

                _instanceReads.Remove(offset);
            }, () => _instanceReads.Remove(offset));
        }

        return read.WaitAsync(cancellationToken);
    }

    /// <summary>Tag categories that haven't been deleted. <paramref name="refresh"/> works as for the instance.</summary>
    public Task<IReadOnlyList<TagCategory>> GetTagCategoriesAsync(TicketingClient client, bool refresh, CancellationToken cancellationToken)
    {
        TaskCompletionSource<IReadOnlyList<TagCategory>>? mine = null;
        Task<IReadOnlyList<TagCategory>> read;
        lock (_lock)
        {
            if (Usable(_tags, refresh) is IReadOnlyList<TagCategory> cached)
            {
                return Task.FromResult(cached);
            }

            if (_tagRead is null)
            {
                mine = new TaskCompletionSource<IReadOnlyList<TagCategory>>(TaskCreationOptions.RunContinuationsAsynchronously);
                _tagRead = mine.Task;
            }

            read = _tagRead;
        }

        if (mine is not null)
        {
            _ = ReadAsync(mine, async () =>
            {
                ListResponse<TagCategory> r = await client.ListTagCategoriesAsync(CancellationToken.None);
                return (IReadOnlyList<TagCategory>)(r.Items ?? []).Where(c => c.Deleted != true).ToList();
            }, categories =>
            {
                _tags = _ttl > TimeSpan.Zero ? NewEntry(categories) : null;
                _tagRead = null;
            }, () => _tagRead = null);
        }

        return read.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Performs one shared read. It isn't tied to the token of the caller that started it, since others may be waiting
    /// for the same answer and one caller giving up mustn't cancel theirs; the client's request timeout bounds it. A
    /// failed read leaves the cached copy in place.
    /// </summary>
    private async Task ReadAsync<T>(TaskCompletionSource<T> result, Func<Task<T>> read, Action<T> store, Action forget)
    {
        try
        {
            T value = await read();
            lock (_lock)
            {
                store(value);
            }

            result.SetResult(value);
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                forget();
            }

            result.SetException(ex);
        }
    }

    private T? Usable<T>(Entry<T>? entry, bool refresh)
        where T : class
    {
        if (entry is null)
        {
            return null;
        }

        DateTimeOffset now = _time.GetUtcNow();
        return (refresh ? now - entry.Read < MinRefreshInterval : entry.Expires > now) ? entry.Value : null;
    }

    private Entry<T> NewEntry<T>(T value)
    {
        DateTimeOffset now = _time.GetUtcNow();
        return new Entry<T>(value, now + _ttl, now);
    }

    private sealed record Entry<T>(T Value, DateTimeOffset Expires, DateTimeOffset Read);
}
