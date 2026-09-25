using ModelContextProtocol;
using TeamsWork.Ticketing.Mcp.Tools;

namespace TeamsWork.Ticketing.Mcp.Tests;

public sealed class ToolValidationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1839")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void RequireGuid_rejects_invalid(string? value)
    {
        McpException ex = Assert.Throws<McpException>(() => ToolValidation.RequireGuid(value, "ticketId"));
        Assert.Contains("ticketId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireGuid_accepts_valid()
    {
        Guid g = ToolValidation.RequireGuid("3FA85F64-5717-4562-B3FC-2C963F66AFA6", "ticketId");
        Assert.Equal(Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6"), g);
    }

    [Fact]
    public void OptionalEnum_is_case_insensitive_and_canonical()
    {
        Assert.Equal("Urgent", ToolValidation.OptionalEnum("urgent", "priority", ToolValidation.Priorities));
        Assert.Equal("In Progress", ToolValidation.OptionalEnum("in progress", "status", ToolValidation.LegacyStatuses));
        Assert.Null(ToolValidation.OptionalEnum("  ", "priority", ToolValidation.Priorities));
        Assert.Throws<McpException>(() => ToolValidation.OptionalEnum("Critical", "priority", ToolValidation.Priorities));
    }

    [Fact]
    public void OptionalDateOnly_rejects_datetime_values()
    {
        Assert.Equal("2026-04-01", ToolValidation.OptionalDateOnly("2026-04-01", "expectedDate"));
        Assert.Throws<McpException>(() => ToolValidation.OptionalDateOnly("2026-04-01T17:00:00Z", "expectedDate"));
        Assert.Throws<McpException>(() => ToolValidation.OptionalDateOnly("04/01/2026", "expectedDate"));
    }

    [Fact]
    public void OptionalDateTime_expands_dates_and_accepts_api_format()
    {
        Assert.Equal("2026-04-01T00:00:00", ToolValidation.OptionalDateTime("2026-04-01", "createdAfter"));
        Assert.Equal("2026-04-01T09:30:00", ToolValidation.OptionalDateTime("2026-04-01T09:30:00", "createdAfter"));
        Assert.Throws<McpException>(() => ToolValidation.OptionalDateTime("2026-04-01T09:30:00Z", "createdAfter"));
        Assert.Throws<McpException>(() => ToolValidation.OptionalDateTime("yesterday", "createdAfter"));
    }

    [Fact]
    public void ResolvePageSize_applies_default_and_cap()
    {
        Assert.Equal(20, ToolValidation.ResolvePageSize(null, 20, 100));
        Assert.Equal(100, ToolValidation.ResolvePageSize(5000, 20, 100));
        Assert.Equal(7, ToolValidation.ResolvePageSize(7, 20, 100));
        Assert.Throws<McpException>(() => ToolValidation.ResolvePageSize(0, 20, 100));
    }

    [Fact]
    public void OptionalSelect_validates_fields()
    {
        Assert.Equal("id,title,status", ToolValidation.OptionalSelect(" id, title ,status"));
        McpException ex = Assert.Throws<McpException>(() => ToolValidation.OptionalSelect("id,ticketNo"));
        Assert.Contains("ticketNo", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireHttpUrl_rejects_non_http()
    {
        Assert.Equal("https://contoso.sharepoint.com/doc", ToolValidation.RequireHttpUrl("https://contoso.sharepoint.com/doc", "url").ToString());
        Assert.Throws<McpException>(() => ToolValidation.RequireHttpUrl("javascript:alert(1)", "url"));
        Assert.Throws<McpException>(() => ToolValidation.RequireHttpUrl("file:///c:/secret", "url"));
        Assert.Throws<McpException>(() => ToolValidation.RequireHttpUrl("not a url", "url"));
    }

    [Fact]
    public void RequireText_enforces_length()
    {
        Assert.Equal("ok", ToolValidation.RequireText("  ok ", "title"));
        Assert.Throws<McpException>(() => ToolValidation.RequireText(" ", "title"));
        Assert.Throws<McpException>(() => ToolValidation.RequireText(new string('x', 501), "title", 500));
    }
}
