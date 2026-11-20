using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// Previewed bulk changes waiting for confirmation, keyed by an unguessable token. A token belongs to the caller who
/// previewed it, must be confirmed within <see cref="Lifetime"/>, and is used once: confirming it again returns what the
/// first confirmation did rather than doing it twice. A confirmation under way is kept until it finishes, however late
/// it started, and its result for <see cref="Lifetime"/> after, so a caller whose answer was lost can still read it.
/// Held in memory, which suits the single replica the server runs as; a restart loses pending tokens, and the caller
/// previews again. Adding, evicting and expiring take one lock, so two previews can't both slip under a cap.
/// </summary>
public sealed class BulkStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    // Far more than one person has in flight. A caller who previews in a loop pushes out only their own oldest tokens,
    // never anyone else's; the overall bound only limits what many callers together can make the server hold.
    internal const int MaxBatchesPerOwner = 20;
    private const int MaxBatches = 5000;

    private readonly ConcurrentDictionary<string, Batch> _batches = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly int _maxPerOwner;
    private readonly int _max;
    private long _added;   // the order batches were stored in, so "oldest" never depends on two having the same deadline

    // Confirmations take a ticket one at a time, across batches. Striped, so the memory stays fixed; two tickets sharing
    // a stripe only wait on each other for a moment.
    private readonly SemaphoreSlim[] _ticketGates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public BulkStore(TimeProvider? time = null)
        : this(time, MaxBatchesPerOwner, MaxBatches)
    {
    }

    /// <summary>With other caps, for tests.</summary>
    internal BulkStore(TimeProvider? time, int maxPerOwner, int max)
    {
        _time = time ?? TimeProvider.System;
        _maxPerOwner = maxPerOwner;
        _max = max;
    }

    internal DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>
    /// Stores a batch and returns its token, or refuses with a message saying why. Room is made only from the caller's
    /// own batches, never anyone else's: their oldest unconfirmed ones, the likeliest to have been abandoned. A batch
    /// being confirmed, or finished with a result still to be had, is kept, so when those fill the caller's places a
    /// preview is refused, and so it is when the store as a whole is full. A <paramref name="resume"/> token, for the
    /// rest of a batch a confirmation stopped part way, may also push out the caller's oldest finished result: a lost
    /// answer from long ago matters less than the work still to do, and this keeps a confirmation retried without
    /// progress (out of quota, say) within the caller's places.
    /// </summary>
    internal string Add(Batch batch, bool resume = false)
    {
        lock (_gate)
        {
            Sweep();
            if (!Evict(_batches.Where(b => b.Value.Owner == batch.Owner).ToList(), _maxPerOwner, resume))
            {
                throw new McpException(
                    $"You have {_maxPerOwner} bulk updates being applied or finished in the last {Lifetime.TotalMinutes:0} minutes, " +
                    "and their results are kept for that long. Try again once one has finished or lapsed.");
            }

            if (_batches.Count >= _max)
            {
                throw new McpException("The server is holding as many bulk updates as it can right now. Try again in a few minutes.");
            }

            string token = NewToken();
            batch.Order = ++_added;
            _batches[token] = batch;
            return token;
        }
    }

    /// <summary>The batch for a token while it can still be confirmed, is being confirmed, or has a result to return.</summary>
    internal Batch? Find(string token)
    {
        lock (_gate)
        {
            Sweep();
            return _batches.TryGetValue(token, out Batch? batch) && batch.IsLive(Now) ? batch : null;
        }
    }

    /// <summary>
    /// Drops the oldest unconfirmed batches, then, when <paramref name="finishedToo"/>, the oldest finished ones, until
    /// there is room for one more; whether there is. A batch being confirmed is never dropped.
    /// </summary>
    private bool Evict(List<KeyValuePair<string, Batch>> batches, int max, bool finishedToo)
    {
        int over = batches.Count - max + 1;
        foreach (KeyValuePair<string, Batch> old in batches.OrderBy(b => b.Value.IsDone).ThenBy(b => b.Value.Order))
        {
            if (over <= 0)
            {
                break;
            }

            if (old.Value.TryEvict(finishedToo))
            {
                _batches.TryRemove(old.Key, out _);
                over--;
            }
        }

        return over <= 0;
    }

    // Under _gate. An unconfirmed batch past its deadline is claimed before it goes, so one whose confirmation has just
    // started stays.
    private void Sweep()
    {
        DateTimeOffset now = Now;
        foreach (KeyValuePair<string, Batch> b in _batches)
        {
            if (!b.Value.IsLive(now) && (b.Value.IsDone || b.Value.IsEvicted || b.Value.TryEvict()))
            {
                _batches.TryRemove(b.Key, out _);
            }
        }
    }

    /// <summary>The gate a confirmation holds while it re-reads and writes one ticket.</summary>
    internal SemaphoreSlim TicketGate(Guid ticket) => _ticketGates[(ticket.GetHashCode() & int.MaxValue) % _ticketGates.Length];

    // 128 random bits, URL-safe.
    private static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    internal static string NewBatchId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    /// <summary>
    /// A previewed change: who may confirm it, until when, the change, and the tickets with what each looked like when
    /// previewed. <see cref="TryStart"/> lets one confirmation run it; <see cref="Result"/> is what that confirmation did.
    /// </summary>
    internal sealed class Batch(string owner, DateTimeOffset expires, string batchId, BulkPlan plan, IReadOnlyList<BulkItem> items)
    {
        private const int Waiting = 0, Running = 1, Done = 2, Evicted = 3;
        private int _state = Waiting;

        public string Owner { get; } = owner;

        public DateTimeOffset Expires { get; } = expires;

        /// <summary>When the store took it, relative to the others; set under the store's lock.</summary>
        public long Order { get; set; }

        public string BatchId { get; } = batchId;

        public BulkPlan Plan { get; } = plan;

        public IReadOnlyList<BulkItem> Items { get; } = items;

        public JsonObject? Result { get; private set; }

        private DateTimeOffset? _finished;

        /// <summary>
        /// Whether the token still means something: an unconfirmed batch until its deadline, one being confirmed until
        /// that finishes, and a finished one's result for <see cref="Lifetime"/> after.
        /// </summary>
        public bool IsLive(DateTimeOffset now) => Volatile.Read(ref _state) switch
        {
            Waiting => Expires > now,
            Running => true,
            Done => _finished is DateTimeOffset at && at + Lifetime > now,
            _ => false,
        };

        public bool IsDone => Volatile.Read(ref _state) == Done;

        public bool IsEvicted => Volatile.Read(ref _state) == Evicted;

        /// <summary>Claims the batch for one confirmation; false once another has it, or it has been evicted.</summary>
        public bool TryStart() => Interlocked.CompareExchange(ref _state, Running, Waiting) == Waiting;

        /// <summary>
        /// Claims an unconfirmed batch for eviction, or a finished one when <paramref name="finishedToo"/>; one being
        /// confirmed stays. Atomic with <see cref="TryStart"/>, so the two can't both win.
        /// </summary>
        public bool TryEvict(bool finishedToo = false) =>
            Interlocked.CompareExchange(ref _state, Evicted, Waiting) == Waiting ||
            (finishedToo && Interlocked.CompareExchange(ref _state, Evicted, Done) == Done);

        public void Finish(JsonObject result, DateTimeOffset now)
        {
            Result = result;
            _finished = now;
            Volatile.Write(ref _state, Done);
        }
    }
}

/// <summary>
/// The change a batch makes, the same on every ticket. The comment goes with a status change when it is public. The
/// caller's time zone offset goes with each write, as the API stores a due date as midnight in it.
/// </summary>
internal sealed record BulkPlan(
    string? Status,
    string? Resolution,
    TicketUser? Assignee,
    string? Priority,
    string? ExpectedDate,
    string? CommentText,
    bool CommentIsPrivate,
    int? TimezoneOffset);

/// <summary>
/// One ticket of a batch. <see cref="Version"/> is how the ticket stood at preview, so a ticket changed since is skipped.
/// <see cref="StatusValue"/> is what the API takes for the status change on this ticket (custom workflows name states by
/// ID), and <see cref="Tags"/> its whole new tag list; each is null when that part doesn't change on this ticket.
/// </summary>
internal sealed record BulkItem(
    Guid Id,
    int? TicketNo,
    TicketVersion Version,
    string? StatusValue,
    IReadOnlyList<TicketTag>? Tags,
    bool ChangesAssignee,
    bool ChangesPriority,
    bool ChangesExpectedDate);

/// <summary>
/// How a ticket stood when read: its _ts, which moves with every change and comment, and the fields a batch reads or
/// changes (state, assignee, priority, due date, title, the last update to the millisecond, the description when the
/// read has it, and the tags). Two versions match only when both carry _ts and it is the same, and every field both
/// carry is the same: _ts counts whole seconds, so the fields catch an edit in the same second as the preview. A
/// version without _ts can't show that no comment came in between, so it never matches: that ticket is skipped (whole
/// list rows don't carry _ts, so a preview reading them takes it from a selected read made first). Tags
/// compare as a list, and by category too when both reads carry categories (the API often returns plain texts). A live
/// check found list and full reads agree on every one of these.
/// </summary>
internal sealed record TicketVersion(long? Ts, string Fields, string? Description, string TagTexts, string? TagKeys)
{
    public static TicketVersion Of(Ticket t)
    {
        List<(string? Category, string Text)> tags = TagsOf(t.Tags);
        return new(
            t.Ts,
            JsonSerializer.Serialize(new[]
            {
                t.Status?.Trim(),
                t.Assignee?.Id?.Trim().ToLowerInvariant(),
                t.Assignee?.Email?.Trim().ToLowerInvariant(),
                TicketViews.Priority(t.Priority),
                t.ExpectedDate?.Trim(),
                t.Title?.Trim(),
                Waiting.Parse(t.LastUpdatedOn)?.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? t.LastUpdatedOn,
            }),
            // A selected list row has no description; a whole ticket does. Compared when both reads have one.
            t.Description is string text ? Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))) : null,
            // Serialized lists, not joined text, so one tag "Network,VPN" isn't two tags "Network" and "VPN".
            JsonSerializer.Serialize(tags.Select(x => x.Text.ToLowerInvariant()).Order(StringComparer.Ordinal)),
            tags.All(x => x.Category is not null)
                ? JsonSerializer.Serialize(tags.Select(x => $"{x.Category!.ToLowerInvariant()}\u001f{x.Text.ToLowerInvariant()}").Order(StringComparer.Ordinal))
                : null);
    }

    /// <summary>Whether both reads carry _ts, without which nothing shows that no comment came in between.</summary>
    public bool Comparable(TicketVersion other) => Ts is not null && other.Ts is not null;

    public bool Matches(TicketVersion other) =>
        Comparable(other) && Ts == other.Ts &&
        string.Equals(Fields, other.Fields, StringComparison.Ordinal) &&
        (Description is null || other.Description is null || string.Equals(Description, other.Description, StringComparison.Ordinal)) &&
        string.Equals(TagTexts, other.TagTexts, StringComparison.Ordinal) &&
        (TagKeys is null || other.TagKeys is null || string.Equals(TagKeys, other.TagKeys, StringComparison.Ordinal));
    /// <summary>A ticket's tags in either shape the API uses: plain texts, or {tagCategoryId, text}.</summary>
    private static List<(string? Category, string Text)> TagsOf(JsonElement? tags)
    {
        var list = new List<(string?, string)>();
        if (tags is not { ValueKind: JsonValueKind.Array } array)
        {
            return list;
        }

        foreach (JsonElement tag in array.EnumerateArray())
        {
            if (tag.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(tag.GetString()))
            {
                list.Add((null, tag.GetString()!.Trim()));
            }
            else if (tag.ValueKind == JsonValueKind.Object && tag.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String &&
                     !string.IsNullOrWhiteSpace(text.GetString()))
            {
                string? category = tag.TryGetProperty("tagCategoryId", out JsonElement c) && c.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(c.GetString())
                    ? c.GetString()!.Trim()
                    : null;
                list.Add((category, text.GetString()!.Trim()));
            }
        }

        return list;
    }
}