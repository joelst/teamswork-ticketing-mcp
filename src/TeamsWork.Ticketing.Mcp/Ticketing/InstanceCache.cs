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
/// Because the cache is shared, no caller can empty it or make it re-read at will:
/// <list type="bullet">
///   <item>a copy is replaced only once a fresh one has been read;</item>
///   <item>a refresh is honoured at most once per <see cref="MinRefreshInterval"/> for the whole cache (not per copy, so
///   cycling time zone offsets doesn't multiply it); within that interval it is an ordinary read. The slot is taken when
///   a refresh starts, so while one is out, a refresh asked for at the same offset joins it and one at another offset is
///   an ordinary read;</item>
///   <item>the caller who starts a read is charged for it against their own quota share, and refused alone if it is
///   used up; callers who join a read in flight share it at no charge and never see another caller's quota error;</item>
///   <item>a successful refresh starts a new generation: reads that began before it can't store their older copy, and new
///   callers don't join them.</item>
/// </list>
/// </para>
/// </summary>
public sealed class InstanceCache
{
    /// <summary>How often a refresh is honoured, for the whole cache.</summary>
    public static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _ttl;
    private readonly TimeProvider _time;
    private readonly TimeZoneOffsetResolver _timeZones;
    private readonly UpstreamQuota? _quota;
    private readonly IUpstreamCaller? _caller;
    private readonly object _lock = new();

    // Instance responses carry times in the requested offset, so each offset is cached separately.
    private readonly Dictionary<int, Entry<Instance>> _instances = [];
    private readonly Dictionary<(int Offset, bool Refresh), InFlight<Instance>> _instanceReads = [];
    private int _instanceGeneration;
    private DateTimeOffset _lastInstanceRefresh = DateTimeOffset.MinValue;
    private int? _instanceRefreshOffset; // the offset of the honoured refresh that is out, if any

    private Entry<IReadOnlyList<TagCategory>>? _tags;
    private readonly Dictionary<bool, InFlight<IReadOnlyList<TagCategory>>> _tagReads = [];
    private int _tagGeneration;
    private DateTimeOffset _lastTagRefresh = DateTimeOffset.MinValue;

    public InstanceCache(
        IOptions<TicketingOptions> options,
        TimeZoneOffsetResolver timeZones,
        TimeProvider? time = null,
        UpstreamQuota? quota = null,
        IUpstreamCaller? caller = null)
    {
        _ttl = TimeSpan.FromSeconds(options.Value.InstanceCacheSeconds);
        _timeZones = timeZones;
        _time = time ?? TimeProvider.System;
        _quota = quota;
        _caller = caller;
    }

    /// <summary>
    /// The instance settings. <paramref name="refresh"/> re-reads them unless the cache was refreshed in the last
    /// <see cref="MinRefreshInterval"/>, and a successful re-read replaces every offset's copy, so later name lookups
    /// (which may use another offset) see the change too.
    /// </summary>
    public Task<Instance> GetInstanceAsync(TicketingClient client, int? timezoneOffset, bool refresh, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); // a caller that has given up starts no read
        int offset = _timeZones.Resolve(timezoneOffset);
        return GetAsync(
            _instanceReads,
            refreshing => (offset, refreshing),
            refreshing =>
            {
                bool honoured = refreshing && _time.GetUtcNow() - _lastInstanceRefresh >= MinRefreshInterval &&
                                (_instanceRefreshOffset is null || _instanceRefreshOffset == offset);
                return (honoured, Usable(_instances.GetValueOrDefault(offset), honoured), _instanceGeneration);
            },
            started => _instanceRefreshOffset = started ? offset : null,
            () => client.GetInstanceAsync(offset, CancellationToken.None, shared: true),
            (instance, honoured, generation) =>
            {
                if (honoured)
                {
                    // Every copy predates this read now, whatever its offset.
                    _instanceGeneration++;
                    _lastInstanceRefresh = _time.GetUtcNow();
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
            },
            refresh,
            cancellationToken);
    }

    /// <summary>Tag categories that haven't been deleted. <paramref name="refresh"/> works as for the instance.</summary>
    public Task<IReadOnlyList<TagCategory>> GetTagCategoriesAsync(TicketingClient client, bool refresh, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return GetAsync(
            _tagReads,
            refreshing => refreshing,
            refreshing =>
            {
                bool honoured = refreshing && _time.GetUtcNow() - _lastTagRefresh >= MinRefreshInterval;
                return (honoured, Usable(_tags, honoured), _tagGeneration);
            },
            _ => { }, // one copy, so a refresh already out is always joined
            async () =>
            {
                ListResponse<TagCategory> r = await client.ListTagCategoriesAsync(CancellationToken.None, shared: true);
                return (IReadOnlyList<TagCategory>)(r.Items ?? []).Where(c => c.Deleted != true).ToList();
            },
            (categories, honoured, generation) =>
            {
                if (honoured)
                {
                    _tagGeneration++;
                    _lastTagRefresh = _time.GetUtcNow();
                }
                else if (generation != _tagGeneration)
                {
                    return;
                }

                _tags = _ttl > TimeSpan.Zero ? NewEntry(categories) : null;
            },
            refresh,
            cancellationToken);
    }

    /// <summary>
    /// The shared logic: answer from the cache, or join a read of the current generation, or start one. Starting is
    /// atomic: the read is reserved under the lock before its starter is charged, so two callers can't both decide to
    /// start one. If the starter's share is used up, the reservation is withdrawn and the starter alone is refused;
    /// callers who had joined it try again on their own account (joining another read, or starting and paying for one)
    /// rather than inheriting that refusal. <paramref name="refreshSlot"/> is told, under the lock, when an honoured
    /// refresh starts and when it ends however it ends, so the next caller's state sees it.
    /// </summary>
    private async Task<T> GetAsync<TKey, T>(
        Dictionary<TKey, InFlight<T>> reads,
        Func<bool, TKey> keyOf,
        Func<bool, (bool Honoured, T? Cached, int Generation)> state,
        Action<bool> refreshSlot,
        Func<Task<T>> read,
        Action<T, bool, int> store,
        bool refresh,
        CancellationToken cancellationToken)
        where TKey : notnull
        where T : class
    {
        while (true)
        {
            TaskCompletionSource<T>? mine = null;
            Task<T> task;
            TKey key;
            bool honoured;
            int generation;
            lock (_lock)
            {
                (honoured, T? cached, generation) = state(refresh);
                if (cached is not null)
                {
                    return cached;
                }

                key = keyOf(honoured);
                if (reads.TryGetValue(key, out InFlight<T>? inFlight) && inFlight.Generation == generation)
                {
                    task = inFlight.Task;
                }
                else
                {
                    mine = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                    task = mine.Task;
                    reads[key] = new InFlight<T>(task, generation);
                    if (honoured)
                    {
                        refreshSlot(true);
                    }
                }
            }

            if (mine is not null)
            {
                // Called under the lock on every path that ends this read.
                void Forget()
                {
                    if (reads.TryGetValue(key, out InFlight<T>? current) && ReferenceEquals(current.Task, mine.Task))
                    {
                        reads.Remove(key);
                    }

                    if (honoured)
                    {
                        refreshSlot(false);
                    }
                }

                try
                {
                    _quota?.Acquire(_caller?.Key)?.Dispose();
                }
                catch (TicketingApiException refused) when (refused.QuotaRefused)
                {
                    lock (_lock)
                    {
                        Forget();
                    }

                    mine.SetException(refused); // callers who joined see a refusal and try for themselves
                    throw;
                }

                _ = ReadAsync(mine, read, value => store(value, honoured, generation), Forget);
            }

            try
            {
                return await task.WaitAsync(cancellationToken);
            }
            catch (TicketingApiException ex) when (mine is null && ex.QuotaRefused)
            {
                // The caller who started the read was refused before it went out: try again on this caller's account.
            }
        }
    }

    /// <summary>
    /// Performs one shared read. It isn't tied to the token of the caller that started it, since others may be waiting
    /// for the same answer and one caller giving up mustn't cancel theirs; the client's request timeout bounds it. A
    /// failed read leaves the cached copy in place. Every path completes <paramref name="result"/>, so no waiter hangs,
    /// and the in-flight entry is removed (only if it is still this read's) before the result is published.
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
                forget();
                store(value);
            }
        }
        catch (Exception ex)
        {
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

        // An honoured refresh re-reads unless the copy is itself only moments old; an ordinary read uses any current copy.
        DateTimeOffset now = _time.GetUtcNow();
        bool current = entry.Expires > now;
        return (refresh ? current && now - entry.Read < MinRefreshInterval : current) ? entry.Value : null;
    }

    private Entry<T> NewEntry<T>(T value)
    {
        DateTimeOffset now = _time.GetUtcNow();
        return new Entry<T>(value, now + _ttl, now);
    }

    private sealed record Entry<T>(T Value, DateTimeOffset Expires, DateTimeOffset Read);

    private sealed record InFlight<T>(Task<T> Task, int Generation);
}
