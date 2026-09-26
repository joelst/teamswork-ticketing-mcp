using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Ticketing;

/// <summary>
/// Typed client for the TeamsWork Ticketing REST API. This is the only place the API key is used, and it is
/// appended to the outbound query string just before the request is sent. Nothing in this class logs or
/// throws the full request URI.
/// </summary>
public sealed class TicketingClient
{
    private const int MaxAttempts = 3;

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly TicketingOptions _options;
    private readonly TicketingRateLimiter _rateLimiter;
    private readonly TimeZoneOffsetResolver _timeZones;
    private readonly ILogger<TicketingClient> _logger;
    private readonly TimeProvider _timeProvider;

    public TicketingClient(
        HttpClient http,
        IOptions<TicketingOptions> options,
        TicketingRateLimiter rateLimiter,
        TimeZoneOffsetResolver timeZones,
        ILogger<TicketingClient> logger,
        TimeProvider? timeProvider = null)
    {
        _http = http;
        _options = options.Value;
        _rateLimiter = rateLimiter;
        _timeZones = timeZones;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // ---- Tickets ----------------------------------------------------------------------------------------------

    public Task<ListResponse<Ticket>> ListTicketsAsync(TicketListQuery q, CancellationToken cancellationToken)
    {
        var query = new List<KeyValuePair<string, string?>>
        {
            new("limit", q.Limit?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("offset", q.Offset?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("search", q.Search),
            new("title", q.Title),
            new("status", q.Status),
            new("statusId", q.StatusId),
            new("isResolved", q.IsResolved switch { true => "true", false => "false", null => null }),
            new("priority", q.Priority),
            new("tags", q.Tags),
            new("orderBy", q.OrderBy),
            new("order", q.Order),
            new("select", q.Select),
            new("createdAfter", q.CreatedAfter),
            new("createdBefore", q.CreatedBefore),
            new("expectedDateAfter", q.ExpectedDateAfter),
            new("expectedDateBefore", q.ExpectedDateBefore),
            new("lastUpdateAfter", q.LastUpdateAfter),
            new("lastUpdateBefore", q.LastUpdateBefore),
            new("include", q.IncludeHtml ? "description_HTML" : null),
        };

        return SendAsync<ListResponse<Ticket>>(HttpMethod.Get, "tickets", query, body: null, q.ContinuationToken, includeTimezone: true, q.TimezoneOffset, cancellationToken);
    }

    public async Task<Ticket> GetTicketAsync(Guid ticketId, bool includeHtml, int? timezoneOffset, CancellationToken cancellationToken)
    {
        var query = new List<KeyValuePair<string, string?>> { new("include", includeHtml ? "description_HTML" : null) };
        ItemResponse<Ticket> r = await SendAsync<ItemResponse<Ticket>>(HttpMethod.Get, $"tickets/{ticketId:D}", query, null, null, true, timezoneOffset, cancellationToken);
        return r.Item ?? throw new TicketingApiException(HttpStatusCode.NotFound, "The Ticketing API returned no ticket for that ID.");
    }

    public async Task<Ticket> CreateTicketAsync(TicketWrite ticket, TicketUser actor, bool includeHtml, int? timezoneOffset, CancellationToken cancellationToken)
    {
        var query = new List<KeyValuePair<string, string?>> { new("include", includeHtml ? "description_HTML" : null) };
        ItemResponse<Ticket> r = await SendAsync<ItemResponse<Ticket>>(HttpMethod.Post, "tickets", query, new InsertTicketRequest(ticket, actor), null, true, timezoneOffset, cancellationToken);
        return r.Item ?? throw new TicketingApiException(
            "The Ticketing API reported success but returned no ticket. This is known to happen when 'expectedDate' is not " +
            "a plain YYYY-MM-DD date while custom fields are also supplied. Check the inputs and try again.");
    }

    public async Task<Ticket> UpdateTicketAsync(Guid ticketId, TicketWrite ticket, TicketUser actor, bool includeHtml, int? timezoneOffset, CancellationToken cancellationToken)
    {
        var query = new List<KeyValuePair<string, string?>> { new("include", includeHtml ? "description_HTML" : null) };
        ItemResponse<Ticket> r = await SendAsync<ItemResponse<Ticket>>(HttpMethod.Put, $"tickets/{ticketId:D}", query, new UpdateTicketRequest(ticket, actor), null, true, timezoneOffset, cancellationToken);
        return r.Item ?? throw new TicketingApiException("The Ticketing API reported success but returned no ticket.");
    }

    public async Task<Ticket> UpdateTicketStatusAsync(Guid ticketId, string status, string? resolution, string? comment, TicketUser actor, int? timezoneOffset, CancellationToken cancellationToken)
    {
        var body = new UpdateTicketStatusRequest(status, resolution, comment, actor);
        ItemResponse<Ticket> r = await SendAsync<ItemResponse<Ticket>>(HttpMethod.Put, $"tickets/{ticketId:D}/status", [], body, null, true, timezoneOffset, cancellationToken);
        return r.Item ?? throw new TicketingApiException("The Ticketing API reported success but returned no ticket.");
    }

    // ---- Activities -------------------------------------------------------------------------------------------

    public Task<ListResponse<Activity>> ListActivitiesAsync(Guid ticketId, bool includeHtml, int? limit, string? continuationToken, CancellationToken cancellationToken)
    {
        var query = new List<KeyValuePair<string, string?>>
        {
            new("include", includeHtml ? "comment_HTML" : null),
            new("limit", limit?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        return SendAsync<ListResponse<Activity>>(HttpMethod.Get, $"tickets/{ticketId:D}/activities", query, null, continuationToken, includeTimezone: false, null, cancellationToken);
    }

    public async Task<CommentActivity> AddCommentAsync(Guid ticketId, string? comment, string? commentHtml, bool isPrivate, TicketUser actor, bool includeHtml, CancellationToken cancellationToken)
    {
        var query = new List<KeyValuePair<string, string?>> { new("include", includeHtml ? "comment_HTML" : null) };
        var body = new InsertCommentRequest(comment, commentHtml, isPrivate, actor);
        ItemResponse<CommentActivity> r = await SendAsync<ItemResponse<CommentActivity>>(HttpMethod.Post, $"tickets/{ticketId:D}/activities", query, body, null, false, null, cancellationToken);
        return r.Item ?? throw new TicketingApiException("The Ticketing API reported success but returned no comment.");
    }

    // ---- Attachments ------------------------------------------------------------------------------------------

    public Task<ListResponse<Attachment>> ListTicketAttachmentsAsync(Guid ticketId, int? timezoneOffset, CancellationToken cancellationToken) =>
        SendAsync<ListResponse<Attachment>>(HttpMethod.Get, $"tickets/{ticketId:D}/attachments", [], null, null, true, timezoneOffset, cancellationToken);

    public async Task<CommentActivity> AddLinkAttachmentsAsync(
        Guid ticketId,
        IReadOnlyList<AttachmentLink> links,
        string? comment,
        string? commentHtml,
        bool isPrivate,
        TicketUser actor,
        bool includeHtml,
        int? timezoneOffset,
        CancellationToken cancellationToken)
    {
        var query = new List<KeyValuePair<string, string?>> { new("include", includeHtml ? "comment_HTML" : null) };
        var body = new InsertAttachmentLinkRequest(comment, commentHtml, links, isPrivate, actor);
        ItemResponse<CommentActivity> r = await SendAsync<ItemResponse<CommentActivity>>(HttpMethod.Post, $"tickets/{ticketId:D}/attachments", query, body, null, true, timezoneOffset, cancellationToken);
        return r.Item ?? throw new TicketingApiException("The Ticketing API reported success but returned no attachment activity.");
    }

    public Task<ListResponse<Attachment>> ListActivityAttachmentsAsync(string activityId, int? timezoneOffset, CancellationToken cancellationToken) =>
        SendAsync<ListResponse<Attachment>>(HttpMethod.Get, $"tickets/activity/{Uri.EscapeDataString(activityId)}/attachments", [], null, null, true, timezoneOffset, cancellationToken);

    // ---- Instance / tags --------------------------------------------------------------------------------------

    public async Task<Instance> GetInstanceAsync(int? timezoneOffset, CancellationToken cancellationToken)
    {
        ItemResponse<Instance> r = await SendAsync<ItemResponse<Instance>>(HttpMethod.Get, "instance", [], null, null, true, timezoneOffset, cancellationToken);
        return r.Item ?? throw new TicketingApiException("The Ticketing API returned no instance details.");
    }

    public Task<ListResponse<TagCategory>> ListTagCategoriesAsync(CancellationToken cancellationToken) =>
        SendAsync<ListResponse<TagCategory>>(HttpMethod.Get, "tags", [], null, null, false, null, cancellationToken);

    // ---- Plumbing ---------------------------------------------------------------------------------------------

    private Uri BuildUri(string path, IEnumerable<KeyValuePair<string, string?>> query, bool includeTimezone, int? timezoneOffset)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new TicketingApiException("Ticketing:ApiKey is not configured on this server.");
        }

        var sb = new StringBuilder(_options.BaseUrl.TrimEnd('/'));
        sb.Append('/').Append(path);
        sb.Append("?key=").Append(Uri.EscapeDataString(_options.ApiKey));

        if (includeTimezone)
        {
            sb.Append("&timezone=").Append(_timeZones.Resolve(timezoneOffset).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        foreach ((string name, string? value) in query)
        {
            if (!string.IsNullOrEmpty(value))
            {
                sb.Append('&').Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
            }
        }

        return new Uri(sb.ToString(), UriKind.Absolute);
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        IEnumerable<KeyValuePair<string, string?>> query,
        object? body,
        string? continuationToken,
        bool includeTimezone,
        int? timezoneOffset,
        CancellationToken cancellationToken)
    {
        Uri uri = BuildUri(path, query, includeTimezone, timezoneOffset);

        // POST creates tickets, comments and attachments. Retrying it after the request may have reached the
        // vendor could create duplicates, so it is only retried when the request provably was not processed.
        bool idempotent = method != HttpMethod.Post;

        for (int attempt = 1; ; attempt++)
        {
            // One permit per upstream call, so retries count against the vendor quota too.
            using var lease = await _rateLimiter.AcquireAsync(cancellationToken);
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (!string.IsNullOrEmpty(continuationToken))
            {
                // TryAddWithoutValidation writes the value to the wire as is, so a CR/LF would inject headers into a
                // request that carries the API key. Tool validation rejects such tokens too; this guards every caller.
                if (!IsSafeHeaderValue(continuationToken))
                {
                    throw new TicketingApiException("The continuation token contains characters that can't be sent. Pass back the value the API returned.");
                }

                request.Headers.TryAddWithoutValidation("continuationToken", continuationToken);
            }

            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
            }

            // HttpClient.Timeout ends once the headers arrive (ResponseHeadersRead), so this also bounds the body read:
            // an upstream that sends headers and then stalls would otherwise hold the request open indefinitely.
            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptTimeout.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));

            long started = _timeProvider.GetTimestamp();
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptTimeout.Token);
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts && (idempotent || IsPreSendFailure(ex)))
            {
                _logger.LogWarning("Ticketing API {Method} /{Path} failed to connect (attempt {Attempt}); retrying. {Error}", method, path, attempt, ex.HttpRequestError);
                await Task.Delay(Backoff(attempt, null), cancellationToken);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new TicketingApiException($"Could not reach the Ticketing API ({ex.HttpRequestError}).", ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TicketingApiException($"The Ticketing API did not respond within {_options.RequestTimeoutSeconds} seconds.", ex);
            }

            using (response)
            {
                TimeSpan elapsed = _timeProvider.GetElapsedTime(started);
                _logger.LogDebug("Ticketing API {Method} /{Path} -> {Status} in {ElapsedMs} ms", method, path, (int)response.StatusCode, (long)elapsed.TotalMilliseconds);

                if (IsRetryable(response.StatusCode, idempotent) && attempt < MaxAttempts)
                {
                    _logger.LogWarning("Ticketing API {Method} /{Path} returned {Status} (attempt {Attempt}); retrying.", method, path, (int)response.StatusCode, attempt);
                    await Task.Delay(Backoff(attempt, response.Headers.RetryAfter), cancellationToken);
                    continue;
                }

                string payload;
                try
                {
                    payload = await ReadBodyAsync(response.Content, _options.MaxResponseBytes, attemptTimeout.Token);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TicketingApiException($"The Ticketing API did not respond within {_options.RequestTimeoutSeconds} seconds.", ex);
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new TicketingApiException(response.StatusCode, DescribeError(response.StatusCode, payload));
                }

                T? result;
                try
                {
                    result = JsonSerializer.Deserialize<T>(payload, JsonOptions);
                }
                catch (JsonException ex)
                {
                    throw new TicketingApiException(response.StatusCode, "The Ticketing API returned a response that could not be parsed as JSON.", ex);
                }

                if (result is null)
                {
                    throw new TicketingApiException(response.StatusCode, "The Ticketing API returned an empty response.");
                }

                // Some endpoints report failures with HTTP 200 and error=true.
                if (result is ListResponse<Ticket> { Error: true } or ItemResponse<Ticket> { Error: true })
                {
                    throw new TicketingApiException(response.StatusCode, ExtractMessage(payload) ?? "The Ticketing API reported an error.");
                }

                if (TryGetErrorFlag(payload, out string? message))
                {
                    throw new TicketingApiException(response.StatusCode, message ?? "The Ticketing API reported an error.");
                }

                return result;
            }
        }
    }

    /// <summary>True when every character is visible ASCII, the only characters an HTTP header value may safely hold.</summary>
    internal static bool IsSafeHeaderValue(string value)
    {
        foreach (char c in value)
        {
            if (c is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads the body as text, refusing to buffer more than <paramref name="maxBytes"/>. Decodes as
    /// ReadAsStringAsync did: the declared charset (UTF-8 when absent or unknown), and a byte-order mark if there is
    /// one, which is also stripped, since JSON parsing fails on a leading U+FEFF.
    /// </summary>
    private static async Task<string> ReadBodyAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maxBytes)
        {
            throw TooLarge(maxBytes);
        }

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw TooLarge(maxBytes);
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, DeclaredEncoding(content.Headers.ContentType?.CharSet), detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static Encoding DeclaredEncoding(string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset.Trim('"', ' '));
            }
            catch (ArgumentException)
            {
                // An unknown charset name; fall back to UTF-8, the JSON default.
            }
        }

        return Encoding.UTF8;
    }

    private static TicketingApiException TooLarge(int maxBytes)
    {
        string limit = maxBytes >= 1024 * 1024 ? $"{maxBytes / (1024.0 * 1024):0.#} MB" : $"{maxBytes / 1024.0:0.#} KB";
        return new($"The Ticketing API response was too large (over {limit}). Request fewer items or use 'select' to return fewer fields.");
    }

    /// <summary>
    /// 429 means the gateway rejected the call without processing it, so it is safe to retry for any method.
    /// 502/503/504 can arrive after the backend already acted, so only idempotent methods retry on them.
    /// </summary>
    private static bool IsRetryable(HttpStatusCode status, bool idempotent) =>
        status == HttpStatusCode.TooManyRequests ||
        (idempotent && status is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout);

    /// <summary>Failures that happen before any request bytes reach the server.</summary>
    private static bool IsPreSendFailure(HttpRequestException ex) =>
        ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError;

    private static TimeSpan Backoff(int attempt, RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
        {
            return delta > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : delta;
        }

        double baseMs = 400 * Math.Pow(2, attempt - 1);
        return TimeSpan.FromMilliseconds(baseMs + Random.Shared.Next(0, 250));
    }

    private static string DescribeError(HttpStatusCode status, string payload)
    {
        string? apiMessage = ExtractMessage(payload);
        string prefix = status switch
        {
            HttpStatusCode.Unauthorized => "The Ticketing API rejected the server's API key (401). Ask an administrator to check the key stored in Key Vault.",
            HttpStatusCode.Forbidden => "The Ticketing API refused the request (403).",
            HttpStatusCode.NotFound => "Not found (404). Check the ticket or activity ID.",
            HttpStatusCode.BadRequest => "The Ticketing API rejected the request (400).",
            HttpStatusCode.TooManyRequests => "The Ticketing API rate limit was hit (429). Wait a minute and retry.",
            _ => $"The Ticketing API returned HTTP {(int)status}.",
        };

        return string.IsNullOrWhiteSpace(apiMessage) ? prefix : $"{prefix} API message: {apiMessage}";
    }

    private static string? ExtractMessage(string payload)
    {
        try
        {
            ErrorResponse? e = JsonSerializer.Deserialize<ErrorResponse>(payload, JsonOptions);
            return e?.Message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetErrorFlag(string payload, out string? message)
    {
        message = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out JsonElement err) &&
                err.ValueKind == JsonValueKind.True)
            {
                if (doc.RootElement.TryGetProperty("message", out JsonElement msg) && msg.ValueKind == JsonValueKind.String)
                {
                    message = msg.GetString();
                }

                return true;
            }
        }
        catch (JsonException)
        {
            // Already deserialized successfully above; ignore.
        }

        return false;
    }
}
