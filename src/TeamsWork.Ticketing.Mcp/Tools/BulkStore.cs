using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// Previewed bulk changes waiting for confirmation, keyed by an unguessable token. A token belongs to the caller who
/// previewed it, lasts <see cref="Lifetime"/>, and is used once: confirming it again returns what the first confirmation
/// did rather than doing it twice. Held in memory, which suits the single replica the server runs as; a restart loses
/// pending tokens, and the caller previews again.
/// </summary>
public sealed class BulkStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    // Far more than one person has in flight. A caller who previews in a loop pushes out only their own oldest tokens,
    // never anyone else's; the overall bound only limits what many callers together can make the server hold.
    internal const int MaxBatchesPerOwner = 20;
    private const int MaxBatches = 5000;

    private readonly ConcurrentDictionary<string, Batch> _batches = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public BulkStore(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    internal DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>Stores a batch and returns its token.</summary>
    internal string Add(Batch batch)
    {
        Sweep();

        // The oldest go first, as the likeliest to have been abandoned; a batch being applied is never dropped.
        Evict(_batches.Where(b => b.Value.Owner == batch.Owner).ToList(), MaxBatchesPerOwner);
        Evict(_batches.ToList(), MaxBatches);

        string token = NewToken();
        _batches[token] = batch;
        return token;
    }

    /// <summary>The batch for a token, unless it has expired or never existed.</summary>
    internal Batch? Find(string token)
    {
        Sweep();
        return _batches.TryGetValue(token, out Batch? batch) && batch.Expires > Now ? batch : null;
    }

    private void Evict(List<KeyValuePair<string, Batch>> batches, int max)
    {
        int over = batches.Count - max + 1;
        foreach (KeyValuePair<string, Batch> old in batches.Where(b => !b.Value.IsRunning).OrderBy(b => b.Value.Expires).Take(Math.Max(0, over)))
        {
            _batches.TryRemove(old.Key, out _);
        }
    }

    private void Sweep()
    {
        DateTimeOffset now = Now;
        foreach (KeyValuePair<string, Batch> b in _batches)
        {
            if (b.Value.Expires <= now)
            {
                _batches.TryRemove(b.Key, out _);
            }
        }
    }

    // 128 random bits, URL-safe.
    private static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    internal static string NewBatchId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    /// <summary>
    /// A previewed change: who may confirm it, until when, the change, and the tickets with what each looked like when
    /// previewed. <see cref="TryStart"/> lets one confirmation run it; <see cref="Result"/> is what that confirmation did.
    /// </summary>
    internal sealed class Batch(string owner, DateTimeOffset expires, string batchId, BulkPlan plan, IReadOnlyList<BulkItem> items)
    {
        private int _state; // 0 waiting, 1 running, 2 done

        public string Owner { get; } = owner;

        public DateTimeOffset Expires { get; } = expires;

        public string BatchId { get; } = batchId;

        public BulkPlan Plan { get; } = plan;

        public IReadOnlyList<BulkItem> Items { get; } = items;

        public JsonObject? Result { get; private set; }

        public bool IsDone => Volatile.Read(ref _state) == 2;

        public bool IsRunning => Volatile.Read(ref _state) == 1;

        public bool TryStart() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

        public void Finish(JsonObject result)
        {
            Result = result;
            Volatile.Write(ref _state, 2);
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
/// How a ticket stood when read: its _ts, which moves with every change and comment, and the fields a batch changes with
/// the last update time. Two versions match when the fields do and, if both have _ts, it does too: _ts counts whole
/// seconds, so a change in the same second as the preview keeps it but still changes the fields. (A comment in that same
/// second changes neither, and would need a finer revision than the API offers.)
/// </summary>
internal sealed record TicketVersion(long? Ts, string Fields)
{
    public static TicketVersion Of(Ticket t) =>
        new(t.Ts, string.Join('|',
            t.Status?.Trim(),
            t.Assignee?.Id?.Trim().ToLowerInvariant(),
            t.Assignee?.Email?.Trim().ToLowerInvariant(),
            TicketViews.Priority(t.Priority),
            t.ExpectedDate?.Trim(),
            string.Join(',', (TicketViews.Tags(t.Tags) ?? []).Select(x => x?.GetValue<string>().ToLowerInvariant()).Order(StringComparer.Ordinal)),
            TicketViews.Time(t.LastUpdatedOn)));

    public bool Matches(TicketVersion other) =>
        string.Equals(Fields, other.Fields, StringComparison.Ordinal) && (Ts is not long mine || other.Ts is not long theirs || mine == theirs);
}