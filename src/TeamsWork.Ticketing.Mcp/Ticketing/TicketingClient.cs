using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
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

    /// <summary>The longest a Retry-After is honoured for between attempts.</summary>
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(10);

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
    private readonly UpstreamQuota? _quota;
    private readonly IUpstreamCaller? _caller;

    public TicketingClient(
        HttpClient http,
        IOptions<TicketingOptions> options,
        TicketingRateLimiter rateLimiter,
        TimeZoneOffsetResolver timeZones,
        ILogger<TicketingClient> logger,
        TimeProvider? timeProvider = null,
        UpstreamQuota? quota = null,
        IUpstreamCaller? caller = null)
    {
        _http = http;
        _options = options.Value;
        _rateLimiter = rateLimiter;
        _timeZones = timeZones;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _quota = quota;
        _caller = caller;
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
        return r.Item ?? throw TicketingApiException.Unknown(
            HttpStatusCode.OK,
            "The Ticketing API reported success but returned no ticket. This is known to happen when 'expectedDate' is not " +
            "a plain YYYY-MM-DD date while custom fields are also supplied.");
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
        // Moving to the same state twice is harmless, but a status change with a note records the note each time.
        ItemResponse<Ticket> r = await SendAsync<ItemResponse<Ticket>>(HttpMethod.Put, $"tickets/{ticketId:D}/status", [], body, null, true, timezoneOffset, cancellationToken, idempotent: comment is null);
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
        return r.Item ?? throw TicketingApiException.Unknown(HttpStatusCode.OK, "The Ticketing API reported success but returned no comment.");
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
        return r.Item ?? throw TicketingApiException.Unknown(HttpStatusCode.OK, "The Ticketing API reported success but returned no attachment activity.");
    }

    /// <summary>Uploads files to a ticket with <c>multipart/form-data</c>, recorded as one attachment activity.</summary>
    public async Task<CommentActivity> UploadFilesAsync(
        Guid ticketId,
        IReadOnlyList<UploadFile> files,
        string? comment,
        string? commentHtml,
        bool isPrivate,
        TicketUser actor,
        bool includeHtml,
        int? timezoneOffset,
        CancellationToken cancellationToken)
    {
        var query = new List<KeyValuePair<string, string?>> { new("include", includeHtml ? "comment_HTML" : null) };

        // Built afresh for each attempt: a sent HttpContent can't be sent again.
        var body = new ContentFactory(() =>
        {
            var form = new MultipartFormDataContent();
            foreach (UploadFile file in files)
            {
                var part = new ByteArrayContent(file.Content);
                part.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType);
                form.Add(FormPart(part, "files", file.FileName));
            }

            if (comment is not null)
            {
                form.Add(FormPart(new StringContent(comment, Encoding.UTF8), "comment"));
            }

            if (commentHtml is not null)
            {
                form.Add(FormPart(new StringContent(commentHtml, Encoding.UTF8), "comment_HTML"));
            }

            // The multipart schema takes these as strings: a "true"/"false" enum and a JSON-encoded user.
            form.Add(FormPart(new StringContent(isPrivate ? "true" : "false"), "isPrivate"));
            form.Add(FormPart(new StringContent(JsonSerializer.Serialize(actor, JsonOptions), Encoding.UTF8), "user"));
            return form;
        });

        ItemResponse<CommentActivity> r = await SendAsync<ItemResponse<CommentActivity>>(HttpMethod.Post, $"tickets/{ticketId:D}/attachments", query, body, null, true, timezoneOffset, cancellationToken);
        return r.Item ?? throw TicketingApiException.Unknown(HttpStatusCode.OK, "The Ticketing API reported success but returned no attachment activity.");
    }

    public Task<ListResponse<Attachment>> ListActivityAttachmentsAsync(string activityId, int? timezoneOffset, CancellationToken cancellationToken) =>
        SendAsync<ListResponse<Attachment>>(HttpMethod.Get, $"tickets/activity/{Uri.EscapeDataString(activityId)}/attachments", [], null, null, true, timezoneOffset, cancellationToken);

    // ---- Instance / tags --------------------------------------------------------------------------------------

    /// <param name="shared">
    /// True for a read made on behalf of every caller (the instance cache): it is charged only to the process-wide quota,
    /// never to the caller who happened to start it, so one caller's used-up share can't fail the others waiting on it.
    /// </param>
    public async Task<Instance> GetInstanceAsync(int? timezoneOffset, CancellationToken cancellationToken, bool shared = false)
    {
        ItemResponse<Instance> r = await SendAsync<ItemResponse<Instance>>(HttpMethod.Get, "instance", [], null, null, true, timezoneOffset, cancellationToken, chargeCaller: !shared);
        return r.Item ?? throw new TicketingApiException("The Ticketing API returned no instance details.");
    }

    /// <param name="shared">As for <see cref="GetInstanceAsync"/>.</param>
    public Task<ListResponse<TagCategory>> ListTagCategoriesAsync(CancellationToken cancellationToken, bool shared = false) =>
        SendAsync<ListResponse<TagCategory>>(HttpMethod.Get, "tags", [], null, null, false, null, cancellationToken, chargeCaller: !shared);

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
        CancellationToken cancellationToken,
        bool? idempotent = null,
        bool chargeCaller = true)
    {
        Uri uri = BuildUri(path, query, includeTimezone, timezoneOffset);
        // Whether repeating a request is harmless belongs to the operation, not the HTTP method. By default a POST (which
        // creates tickets, comments and attachments) isn't, and anything else is; callers say otherwise.
        // The caller is identified once, before any retry, so a retry after the caller's request has ended is still
        // charged to them rather than to nobody.
        string? callerKey = chargeCaller ? _caller?.Key : null;
        return await SendCoreAsync<T>(method, path, uri, body, continuationToken, idempotent ?? method != HttpMethod.Post, callerKey, cancellationToken);
    }

    private async Task<T> SendCoreAsync<T>(
        HttpMethod method,
        string path,
        Uri uri,
        object? body,
        string? continuationToken,
        bool idempotent,
        string? callerKey,
        CancellationToken cancellationToken)
    {
        // A request that isn't idempotent is only retried when it provably wasn't processed.

        for (int attempt = 1; ; attempt++)
        {
            // One permit per upstream call, so retries count against the vendor quota too: first the caller's own share,
            // then the process-wide quota.
            using RateLimitLease? callerLease = _quota?.Acquire(callerKey);
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

            if (body is ContentFactory factory)
            {
                request.Content = factory.Create();
            }
            else if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
            }

            // HttpClient.Timeout ends once the headers arrive (ResponseHeadersRead), so this also bounds the body read:
            // an upstream that sends headers and then stalls would otherwise hold the request open indefinitely.
            // A request that isn't safe to repeat is seen through once it is sent: the caller's cancellation still stops the
            // waiting before it (the rate limiter, a backoff), but not the exchange itself, which would leave a ticket or
            // comment created with no one told. Only the server's own time limit ends it, and a time-out reports the
            // outcome as unknown.
            CancellationToken exchange = idempotent ? cancellationToken : CancellationToken.None;
            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(exchange);
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
                throw Failure(idempotent, mayHaveBeenProcessed: !IsPreSendFailure(ex), null, $"Could not reach the Ticketing API ({ex.HttpRequestError}).", ex);
            }
            catch (TaskCanceledException ex) when (!exchange.IsCancellationRequested)
            {
                throw Failure(idempotent, mayHaveBeenProcessed: true, null, $"The Ticketing API did not respond within {_options.RequestTimeoutSeconds} seconds.", ex);
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
                catch (OperationCanceledException ex) when (!exchange.IsCancellationRequested)
                {
                    // The headers arrived, so the API received the request.
                    throw Failure(idempotent, mayHaveBeenProcessed: true, response.StatusCode, $"The Ticketing API did not respond within {_options.RequestTimeoutSeconds} seconds.", ex);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                    // The connection dropped while the answer was arriving: the request was received, and may have been done.
                    throw Failure(idempotent, mayHaveBeenProcessed: true, response.StatusCode, "The connection to the Ticketing API dropped while its answer was arriving.", ex);
                }
                catch (TicketingApiException ex) when (!idempotent && !ex.OutcomeUnknown)
                {
                    // An answer too large to read, after the request was received.
                    throw TicketingApiException.Unknown(response.StatusCode, ex.Message, ex);
                }

                if (!response.IsSuccessStatusCode)
                {
                    // A 4xx means the API refused the request; a 5xx can come after it already acted.
                    throw Failure(idempotent, mayHaveBeenProcessed: (int)response.StatusCode >= 500, response.StatusCode, DescribeError(response.StatusCode, payload), null);
                }

                T? result;
                try
                {
                    result = JsonSerializer.Deserialize<T>(payload, JsonOptions);
                }
                catch (JsonException ex)
                {
                    throw Failure(idempotent, mayHaveBeenProcessed: true, response.StatusCode, "The Ticketing API returned a response that could not be parsed as JSON.", ex);
                }

                if (result is null)
                {
                    throw Failure(idempotent, mayHaveBeenProcessed: true, response.StatusCode, "The Ticketing API returned an empty response.", null);
                }

                // Some endpoints report failures with HTTP 200 and error=true. The request reached the endpoint, and nothing
                // says an error reported this way means nothing was done, so for a request that isn't safe to repeat it is
                // an unknown outcome (with the API's message), like every other failure after the request arrived.
                if (result is ListResponse<Ticket> { Error: true } or ItemResponse<Ticket> { Error: true })
                {
                    throw Failure(idempotent, mayHaveBeenProcessed: true, response.StatusCode, ExtractMessage(payload) ?? "The Ticketing API reported an error.", null);
                }

                if (TryGetErrorFlag(payload, out string? message))
                {
                    throw Failure(idempotent, mayHaveBeenProcessed: true, response.StatusCode, message ?? "The Ticketing API reported an error.", null);
                }

                return result;
            }
        }
    }

    /// <summary>
    /// Sets a multipart part's Content-Disposition with quoted values. MultipartFormDataContent.Add writes
    /// <c>name=files</c> unquoted (plus a <c>filename*</c> parameter), which the Ticketing API answers with HTTP 500;
    /// <c>name="files"</c> works. File names reach here already stripped of quotes and non-ASCII characters.
    /// </summary>
    private static HttpContent FormPart(HttpContent content, string name, string? fileName = null)
    {
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = $"\"{name}\"",
            FileName = fileName is null ? null : $"\"{fileName}\"",
        };
        return content;
    }

    /// <summary>
    /// The exception for a failed request. For a create request (not idempotent) that may have reached the API, it is
    /// marked <see cref="TicketingApiException.OutcomeUnknown"/> and advises checking before a retry; for anything
    /// else the message is left as is, since repeating it is harmless or the API refused it.
    /// </summary>
    private static TicketingApiException Failure(bool idempotent, bool mayHaveBeenProcessed, HttpStatusCode? status, string message, Exception? inner)
    {
        if (!idempotent && mayHaveBeenProcessed)
        {
            return TicketingApiException.Unknown(status, message, inner);
        }

        return inner is null ? new TicketingApiException(status, message) : new TicketingApiException(status, message, inner);
    }

    /// <summary>A request body that isn't JSON. Called once per attempt, since a sent HttpContent can't be reused.</summary>
    private sealed record ContentFactory(Func<HttpContent> Create);

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

    /// <summary>
    /// Failures that can only happen before any request bytes are sent: the name didn't resolve, the TLS handshake failed
    /// (the request is sent only over an established TLS session), or the TCP connection couldn't be made. The last is
    /// recognised by its cause as well as its category: the handler reports a failed connect as a connection error
    /// wrapping the socket's own error, whereas a connection lost after the request went out surfaces as an I/O error.
    /// Anything else, for a request that isn't safe to repeat, is an unknown outcome rather than proof nothing happened.
    /// </summary>
    internal static bool IsPreSendFailure(HttpRequestException ex) =>
        ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError ||
        (ex.HttpRequestError is HttpRequestError.ConnectionError && ex.InnerException is System.Net.Sockets.SocketException);

    /// <summary>
    /// The longest one request can take, retries included: each attempt may wait a full window for the rate limiter and
    /// then its own time limit, with the longest honoured delay between attempts. A caller that must let a request finish
    /// on its own (after the caller's token no longer applies) bounds it by this, so it isn't cut short mid-exchange.
    /// </summary>
    internal static TimeSpan LongestRequest(TicketingOptions options) =>
        MaxAttempts * TimeSpan.FromSeconds(options.RateLimitWindowSeconds + options.RequestTimeoutSeconds) +
        (MaxAttempts - 1) * MaxRetryDelay +
        TimeSpan.FromSeconds(5);

    private static TimeSpan Backoff(int attempt, RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
        {
            return delta > MaxRetryDelay ? MaxRetryDelay : delta;
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
