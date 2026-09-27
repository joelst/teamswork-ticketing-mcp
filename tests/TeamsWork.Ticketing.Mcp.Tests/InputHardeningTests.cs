using System.Net;
using System.Text.Json;
using ModelContextProtocol;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;
using TeamsWork.Ticketing.Mcp.Tools;

namespace TeamsWork.Ticketing.Mcp.Tests;

/// <summary>What agent-supplied links, people, and emails may contain, and what the API's own messages pass on.</summary>
public sealed class InputHardeningTests
{
    private const string Outsider = "22222222-2222-2222-2222-222222222222";

    private static readonly Instance Listed = JsonSerializer.Deserialize<ItemResponse<Instance>>(
        """{"item":{"id":"i","assignees":{"peoples":[{"id":"11111111-1111-1111-1111-111111111111","name":"Jane Doe","email":"jane.doe@contoso.com"}]}}}""",
        TicketingClient.JsonOptions)!.Item!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Link URLs are sent as escaped, and read as what they open ----------------------------------------------------

    [Theory]
    [InlineData("https://example.com/a%22%3E%3Cscript%3Ealert(1)%3C/script%3E")] // escaped markup
    [InlineData("https://example.com/q?x=\"<>\"")]                               // raw markup
    [InlineData("https://example.com/%E2%80%AEfdp.exe")]                          // escaped right-to-left override
    [InlineData("https://example.com/a b\tc")]                                    // raw space and tab
    public void A_link_is_sent_with_its_escapes_kept(string url)
    {
        string src = new LinkRef(url, "c").ToAttachmentLink(0).Src;

        Assert.DoesNotContain(src, c => c is '"' or '<' or '>' or ' ' or '\t' || c > 0x7F);
    }

    [Theory]
    [InlineData("https://sharepoint.com@evil.example/", "user name or password")]
    [InlineData("https://user:pass@contoso.sharepoint.com/doc", "user name or password")]
    [InlineData("https://раураl.com/x", "ASCII host")] // Cyrillic look-alikes of "paypal"
    public void A_link_that_reads_as_another_site_is_refused(string url, string message)
    {
        McpException ex = Assert.Throws<McpException>(() => new LinkRef(url, "c").ToAttachmentLink(0));

        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_has_a_length_limit()
    {
        string url = "https://example.com/" + new string('a', ToolValidation.MaxUrlLength);

        Assert.Throws<McpException>(() => new LinkRef(url, "c").ToAttachmentLink(0));
        Assert.Equal("https://xn--80aa0cbo65f.com/x", new LinkRef("https://xn--80aa0cbo65f.com/x", "c").ToAttachmentLink(0).Src); // punycode is fine
    }

    // ---- People: control characters can't hide a namesake -------------------------------------------------------------

    [Theory]
    [InlineData("Jane Doe\u0001")]
    [InlineData("Jane\u0007 Doe")]
    [InlineData("Jane Doe\u007F")]
    [InlineData("Jane Doe\u009B")]
    [InlineData("Ja\u0000ne Doe")]
    [InlineData("Jane\tDoe")]
    public void Control_characters_are_refused_in_a_persons_name(string name)
    {
        McpException ex = Assert.Throws<McpException>(() =>
            InstanceLookup.ResolvePerson(new UserRef(Outsider, name, "pat@outside.test"), "requestor", Listed, assigneeOnly: false));

        Assert.Contains("control characters", ex.Message, StringComparison.Ordinal);
    }

    // ---- Emails: one plain mailbox ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("\"jane@evil.com\"@contoso.com")]
    [InlineData("\"<script>\"@contoso.com")]
    [InlineData("\"a,b\"@contoso.com")]
    [InlineData(".jane@contoso.com")]
    [InlineData("jane.@contoso.com")]
    [InlineData("ja..ne@contoso.com")]
    public void A_quoted_or_malformed_local_part_is_refused(string email)
    {
        Assert.Throws<McpException>(() => ToolValidation.RequireEmail(email, "email"));
    }

    [Theory]
    [InlineData("jane.doe@contoso.com")]
    [InlineData("jane+help@contoso.com")]
    [InlineData("o'brien@contoso.com")]
    [InlineData("jörg@contoso.com")] // a mailbox with a letter outside ASCII, as a listed person may have
    public void Ordinary_addresses_still_pass(string email)
    {
        Assert.Equal(email, ToolValidation.RequireEmail(email, "email"));
    }

    // ---- The post-create follow-up can't fail on its own timer --------------------------------------------------------

    [Fact]
    public void The_follow_up_timer_fits_every_valid_configuration()
    {
        // All at their validated extremes: one slot, one permit, the longest window and time limit.
        TicketingOptions o = TestFactory.Options(o =>
        {
            o.MaxConcurrentUpstreamRequests = 1;
            o.RateLimitPermits = 1;
            o.RateLimitWindowSeconds = 3600;
            o.RequestTimeoutSeconds = 300;
        });
        TimeSpan longest = TicketingClient.LongestRequest(o);
        Assert.True(longest > TimeSpan.FromMilliseconds(uint.MaxValue - 1)); // longer than a timer can wait

        using var timer = new CancellationTokenSource(TicketingClient.TimerLimit(longest)); // so it must be capped
        Assert.Equal(TicketingClient.LongestRequest(TestFactory.Options()), TicketingClient.TimerLimit(TicketingClient.LongestRequest(TestFactory.Options())));
    }

    // ---- The API's messages are cleaned before an agent sees them -----------------------------------------------------

    [Theory]
    [InlineData(TestFactory.ApiKey, false)]
    [InlineData("k/ey+with=chars", false)]
    [InlineData("k/ey+with=chars", true)] // as the request URL carries it, which a message may quote
    public async Task The_api_key_is_removed_from_an_api_message(string key, bool escaped)
    {
        string quoted = escaped ? Uri.EscapeDataString(key) : key;
        Assert.True(!escaped || quoted != key); // the escaped case really differs from the plain one
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.BadRequest,
            JsonSerializer.Serialize(new { message = $"bad request to /tickets?key={quoted}" }));

        TicketingApiException ex = await Assert.ThrowsAsync<TicketingApiException>(() =>
            TestFactory.Client(handler, TestFactory.Options(o => o.ApiKey = key)).ListTicketsAsync(new TicketListQuery(), Ct));

        Assert.DoesNotContain(quoted, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[API key]", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_api_message_is_cut_short_and_loses_control_characters()
    {
        string message = "line one\nline two\u0007" + new string('x', 5000);
        var handler = new FakeHttpHandler().Enqueue(HttpStatusCode.OK, JsonSerializer.Serialize(new { items = Array.Empty<object>(), error = true, message }));

        TicketingApiException ex = await Assert.ThrowsAsync<TicketingApiException>(() =>
            TestFactory.Client(handler).ListTicketsAsync(new TicketListQuery(), Ct));

        Assert.DoesNotContain(ex.Message, char.IsControl);
        Assert.True(ex.Message.Length < 1000, ex.Message.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
