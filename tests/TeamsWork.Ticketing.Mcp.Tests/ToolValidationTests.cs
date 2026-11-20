using ModelContextProtocol;
using TeamsWork.Ticketing.Mcp.Tools;

namespace TeamsWork.Ticketing.Mcp.Tests;

public sealed class ToolValidationTests
{
    [Fact]
    public void OptionalEnum_is_case_insensitive_and_canonical()
    {
        Assert.Equal("Urgent", ToolValidation.OptionalEnum("urgent", "priority", ToolValidation.Priorities));
        Assert.Equal("In Progress", ToolValidation.OptionalEnum("in progress", "status", WorkflowStatus.DefaultStates));
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
    public void Date_filters_are_whole_days_only()
    {
        // The API ignores a date filter with a time of day and returns every ticket, so none is passed on.
        Assert.Equal(new DateOnly(2026, 4, 1), ToolValidation.OptionalDateFilter(" 2026-04-01 ", "createdAfter"));
        Assert.Null(ToolValidation.OptionalDateFilter(" ", "createdAfter"));
        Assert.Contains("whole days", Assert.Throws<McpException>(() => ToolValidation.OptionalDateFilter("2026-04-01T09:30:00", "createdAfter")).Message, StringComparison.Ordinal);
        Assert.Throws<McpException>(() => ToolValidation.OptionalDateFilter("2026-04-01T09:30:00Z", "createdAfter"));
        Assert.Throws<McpException>(() => ToolValidation.OptionalDateFilter("2026-02-30", "createdAfter"));
        Assert.Throws<McpException>(() => ToolValidation.OptionalDateFilter("yesterday", "createdAfter"));
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
        McpException ex = Assert.Throws<McpException>(() => ToolValidation.OptionalSelect("id,ticketNumber"));
        Assert.Contains("ticketNumber", ex.Message, StringComparison.Ordinal);
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
