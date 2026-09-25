using System.Collections;
using Microsoft.Extensions.Options;
using TeamsWork.Ticketing.Mcp.Configuration;

namespace TeamsWork.Ticketing.Mcp.Tests;

public sealed class StartupErrorReportTests
{
    private static readonly Hashtable NoEnvironment = new();

    private static OptionsValidationException ValidationFailure(params string[] failures) =>
        new(Options.DefaultName, typeof(TicketingOptions), failures);

    [Fact]
    public void Lists_every_validation_failure_and_where_to_set_values()
    {
        var ex = ValidationFailure("Ticketing:ApiKey is not set.", "Ticketing:ServiceAccount:Id, :Name and :Email must all be set.");

        Assert.True(StartupErrorReport.TryFormat(ex, NoEnvironment, @"C:\secrets\secrets.json", out string message));

        Assert.Contains("  - Ticketing:ApiKey is not set.", message, StringComparison.Ordinal);
        Assert.Contains("  - Ticketing:ServiceAccount:Id, :Name and :Email must all be set.", message, StringComparison.Ordinal);
        Assert.Contains("Ticketing__ApiKey", message, StringComparison.Ordinal);
        Assert.Contains(@"C:\secrets\secrets.json", message, StringComparison.Ordinal);
        Assert.Contains(StartupErrorReport.SetupGuideUrl, message, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", message, StringComparison.Ordinal); // no stack trace
    }

    [Fact]
    public void Formats_startup_configuration_exceptions_and_aggregates_of_them()
    {
        var ex = new AggregateException(new StartupConfigurationException("Local:Port must be between 1 and 65535."), ValidationFailure("Ticketing:ApiKey is not set."));

        Assert.True(StartupErrorReport.TryFormat(ex, NoEnvironment, null, out string message));

        Assert.Contains("Local:Port must be between 1 and 65535.", message, StringComparison.Ordinal);
        Assert.Contains("Ticketing:ApiKey is not set.", message, StringComparison.Ordinal);
        Assert.DoesNotContain("user-secrets file", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_unrelated_exceptions_to_crash_normally()
    {
        Assert.False(StartupErrorReport.TryFormat(new IOException("disk"), NoEnvironment, null, out _));
        Assert.False(StartupErrorReport.TryFormat(new AggregateException(ValidationFailure("x"), new IOException("disk")), NoEnvironment, null, out _));
    }

    [Fact]
    public void Flags_single_underscore_variables_by_name_without_printing_values()
    {
        var env = new Hashtable
        {
            ["TICKETING_APIKEY"] = "secret-value-1",
            ["TICKETING_API_KEY"] = "secret-value-2",
            ["TICKETING_SERVICEACCOUNT_EMAIL"] = "someone@example.test",
            ["Ticketing__ServiceAccount__Id"] = "correct-already",
            ["PATH"] = "unrelated",
        };

        Assert.True(StartupErrorReport.TryFormat(ValidationFailure("Ticketing:ApiKey is not set."), env, null, out string message));

        Assert.Contains("TICKETING_APIKEY  ->  Ticketing__ApiKey", message, StringComparison.Ordinal);
        Assert.Contains("TICKETING_API_KEY  ->  Ticketing__ApiKey", message, StringComparison.Ordinal);
        Assert.Contains("TICKETING_SERVICEACCOUNT_EMAIL  ->  Ticketing__ServiceAccount__Email", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Ticketing__ServiceAccount__Id  ->", message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", message, StringComparison.Ordinal);
        Assert.DoesNotContain("someone@example.test", message, StringComparison.Ordinal);
    }
}
