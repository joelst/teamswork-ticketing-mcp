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
/// fresh one has been read, callers that need the same read at the same time share it, and a refresh is honoured only
/// when the copy is older than <see cref="MinRefreshInterval"/>. A successful refresh starts a new generation: reads
/// that began before it may still answer their own callers, but can't put their older copy back in the cache.
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

    // Instance responses carry times in the requested offset, so each offset is cached separately. A refresh never
    // joins an ordinary read (which may have started before the settings changed), so in-flight reads are keyed by
    // whether they are refreshes.
    private readonly Dictionary<int, Entry<Instance>> _instances = [];
    private readonly Dictionary<(int Offset, bool Refresh), Task<Instance>> _instanceReads = [];
    private int _instanceGeneration;

    private Entry<IReadOnlyList<TagCategory>>? _tags;
    private readonly Dictionary<bool, Task<IReadOnlyList<TagCategory>>> _tagReads = [];
    private int _tagGeneration;

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
        cancellationToken.ThrowIfCancellationRequested(); // a caller that has given up starts no read
        int offset = _timeZones.Resolve(timezoneOffset);
        TaskCompletionSource<Instance>? mine = null;
        Task<Instance> read;
        int generation;
        lock (_lock)
        {
            if (Usable(_instances.GetValueOrDefault(offset), refresh) is Instance cached)
            {
                return Task.FromResult(cached);
            }

            generation = _instanceGeneration;
            if (!_instanceReads.TryGetValue((offset, refresh), out read!))
            {
                mine = new TaskCompletionSource<Instance>(TaskCreationOptions.RunContinuationsAsynchronously);
                read = mine.Task;
                _instanceReads[(offset, refresh)] = read;
            }
        }

        if (mine is not null)
        {
            _ = ReadAsync(mine, () => client.GetInstanceAsync(offset, CancellationToken.None, shared: true), instance =>
            {
                _instanceReads.Remove((offset, refresh));
                if (refresh)
                {
                    // Every copy predates this read now, whatever its offset.
                    _instanceGeneration++;
                    _instances.Clear();
                }
                else if (generation != _instanceGeneration)
                {
                    return; // a refresh finished while this read was out: its copy may be older
                }

                if (_ttl > TimeSpan.Zero)
                {
                    _instances[offset] = NewEntry(instance);
                }
            }, () => _instanceReads.Remove((offset, refresh)));
        }

        return read.WaitAsync(cancellationToken);
    }

    /// <summary>Tag categories that haven't been deleted. <paramref name="refresh"/> works as for the instance.</summary>
    public Task<IReadOnlyList<TagCategory>> GetTagCategoriesAsync(TicketingClient client, bool refresh, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<IReadOnlyList<TagCategory>>? mine = null;
        Task<IReadOnlyList<TagCategory>> read;
        int generation;
        lock (_lock)
        {
            if (Usable(_tags, refresh) is IReadOnlyList<TagCategory> cached)
            {
                return Task.FromResult(cached);
            }

            generation = _tagGeneration;
            if (!_tagReads.TryGetValue(refresh, out read!))
            {
                mine = new TaskCompletionSource<IReadOnlyList<TagCategory>>(TaskCreationOptions.RunContinuationsAsynchronously);
                read = mine.Task;
                _tagReads[refresh] = read;
            }
        }

        if (mine is not null)
        {
            _ = ReadAsync(mine, async () =>
            {
                ListResponse<TagCategory> r = await client.ListTagCategoriesAsync(CancellationToken.None, shared: true);
                return (IReadOnlyList<TagCategory>)(r.Items ?? []).Where(c => c.Deleted != true).ToList();
            }, categories =>
            {
                _tagReads.Remove(refresh);
                if (refresh)
                {
                    _tagGeneration++;
                }
                else if (generation != _tagGeneration)
                {
                    return;
                }

                _tags = _ttl > TimeSpan.Zero ? NewEntry(categories) : null;
            }, () => _tagReads.Remove(refresh));
        }

        return read.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Performs one shared read. It isn't tied to the token of the caller that started it, since others may be waiting
    /// for the same answer and one caller giving up mustn't cancel theirs; the client's request timeout bounds it. A
    /// failed read leaves the cached copy in place. Every path completes <paramref name="result"/>, so no waiter hangs.
    /// </summary>
    private async Task ReadAsync<T>(TaskCompletionSource<T> result, Func<Task<T>> read, Action<T> store, Action forget)
    {
        T value;
        try
        {
            value = await read();
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                forget();
            }

            result.SetException(ex);
            return;
        }

        try
        {
            lock (_lock)
            {
                store(value);
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                forget();
            }

            result.SetException(ex);
            return;
        }

        result.SetResult(value);
    }

    private T? Usable<T>(Entry<T>? entry, bool refresh)
        where T : class
    {
        if (entry is null)
        {
            return null;
        }

        DateTimeOffset now = _time.GetUtcNow();
        // A refresh is answered from the copy only if it is also still current: with a lifetime shorter than the refresh
        // interval, a refresh must never be weaker than an ordinary read.
        bool current = entry.Expires > now;
        return (refresh ? current && now - entry.Read < MinRefreshInterval : current) ? entry.Value : null;
    }

    private Entry<T> NewEntry<T>(T value)
    {
        DateTimeOffset now = _time.GetUtcNow();
        return new Entry<T>(value, now + _ttl, now);
    }

    private sealed record Entry<T>(T Value, DateTimeOffset Expires, DateTimeOffset Read);
}
