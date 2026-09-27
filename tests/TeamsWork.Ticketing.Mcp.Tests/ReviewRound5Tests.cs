using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;
using TeamsWork.Ticketing.Mcp.Tools;
using static TeamsWork.Ticketing.Mcp.Tests.NewToolsTests;

namespace TeamsWork.Ticketing.Mcp.Tests;

/// <summary>Behaviour added in the fifth review round.</summary>
public sealed class ReviewRound5Tests
{
    private const string TicketA = "3fa85f64-5717-4562-b3fc-2c963f66afa6";
    private static readonly TicketUser Actor = new("u1", "Jane Doe", "jane@example.test");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpResponseMessage Reset(HttpRequestMessage _) =>
        throw new HttpRequestException(HttpRequestError.ConnectionError, "The connection was reset.");

    [Fact]
    public async Task A_create_whose_connection_resets_isnt_retried()
    {
        // A reset can come after the body went out, so a create may have happened: never repeated blindly.
        var handler = new FakeHttpHandler()
            .Enqueue(Reset)
            .Enqueue(HttpStatusCode.Created, $$$"""{"item":{"id":"{{{TicketA}}}"}}""");

        TicketingApiException ex = await Assert.ThrowsAsync<TicketingApiException>(() =>
            TestFactory.Client(handler).CreateTicketAsync(new TicketWrite { Title = "t" }, Actor, false, null, Ct));

        Assert.True(ex.OutcomeUnknown);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_read_whose_connection_resets_is_retried()
    {
        var handler = new FakeHttpHandler()
            .Enqueue(Reset)
            .Enqueue(HttpStatusCode.OK, """{"items":[]}""");

        await TestFactory.Client(handler).ListTicketsAsync(new TicketListQuery(), Ct);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_caller_giving_up_during_the_priority_follow_up_still_gets_the_ticket()
    {
        using var caller = new CancellationTokenSource();
        var handler = new FakeHttpHandler()
            .Enqueue(_ =>
            {
                caller.Cancel(); // the caller gives up right after the ticket is created
                return FakeHttpHandler.Json(HttpStatusCode.Created, $$$"""{"item":{"id":"{{{TicketA}}}","ticketNo":5,"priority":"Medium"}}""");
            })
            .Enqueue(HttpStatusCode.OK, $$$"""{"item":{"id":"{{{TicketA}}}","ticketNo":5,"priority":"Low"}}""");
        (TicketingClient client, InstanceCache cache, IOptions<TicketingOptions> options) = Build(handler);

        using JsonDocument doc = JsonDocument.Parse(await new TicketTools(client, new FixedActor(Jane), cache, options).CreateTicket("Hi", priority: "Low", cancellationToken: caller.Token));

        Assert.Equal("Low", doc.RootElement.GetProperty("item").GetProperty("priority").GetString());
        Assert.Equal(2, handler.Requests.Count);
    }
}
