using System.Collections;
using Microsoft.Extensions.Configuration;
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

    [Fact]
    public void Flags_mixed_separators_but_not_names_that_bind_correctly()
    {
        var env = new Hashtable
        {
            ["Ticketing__ServiceAccount_Email"] = "x", // binds to Ticketing:ServiceAccount_Email, so it is ignored
            ["Ticketing:ServiceAccount:Name"] = "x",   // valid where the OS allows ':' in variable names
            ["TICKETING__APIKEY"] = "x",               // valid: keys are case-insensitive
        };

        List<(string Found, string Expected)> misnamed = StartupErrorReport.FindMisnamedVariables(env);

        (string found, string expected) = Assert.Single(misnamed);
        Assert.Equal("Ticketing__ServiceAccount_Email", found);
        Assert.Equal("Ticketing__ServiceAccount__Email", expected);
    }

    [Fact]
    public void Conversion_failures_name_the_setting_without_its_value()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ticketing:MaxPageSize"] = "secret-abc" })
            .Build();
        InvalidOperationException bindError = Assert.ThrowsAny<InvalidOperationException>(() => config.GetSection("Ticketing").Bind(new TicketingOptions()));

        StartupConfigurationException ex = StartupConfigurationException.FromConversionFailure(bindError, "Ticketing");

        Assert.Equal("Ticketing:MaxPageSize has a value that cannot be read as a whole number.", ex.Message);
        Assert.Null(ex.InnerException); // the host logs inner exceptions, and the binder's carry the value
        Assert.DoesNotContain("secret-abc", ex.ToString(), StringComparison.Ordinal);
        Assert.True(StartupErrorReport.TryFormat(ex, NoEnvironment, null, out string message));
        Assert.DoesNotContain("secret-abc", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Conversion_failures_of_nullable_values_name_the_expected_type()
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Local:Port"] = "abc" })
            .Build();

        StartupConfigurationException ex = Assert.Throws<StartupConfigurationException>(
            () => StartupConfigurationException.ReadSetting(() => config.GetValue<int?>("Local:Port"), "Local:Port"));

        Assert.Equal("Local:Port has a value that cannot be read as a whole number.", ex.Message);
    }

    [Fact]
    public void Known_settings_cover_every_settable_option()
    {
        Assert.Contains("Ticketing:MaxPageSize", StartupErrorReport.KnownSettings);
        Assert.Contains("Ticketing:ServiceAccount:Email", StartupErrorReport.KnownSettings);
        Assert.Contains("Entra:PublicBaseUrl", StartupErrorReport.KnownSettings);
        Assert.Contains("Local:Port", StartupErrorReport.KnownSettings);
        Assert.DoesNotContain("Entra:Authority", StartupErrorReport.KnownSettings);                   // computed
        Assert.DoesNotContain("Ticketing:ServiceAccount:IsConfigured", StartupErrorReport.KnownSettings); // computed

        var env = new Hashtable { ["TICKETING_REQUEST_TIMEOUT_SECONDS"] = "x" };
        (_, string expected) = Assert.Single(StartupErrorReport.FindMisnamedVariables(env));
        Assert.Equal("Ticketing__RequestTimeoutSeconds", expected);
    }

    [Theory]
    [InlineData(null, AuthMode.Entra)]
    [InlineData("", AuthMode.Entra)]
    [InlineData(" local ", AuthMode.Local)]
    [InlineData("ENTRA", AuthMode.Entra)]
    public void Auth_mode_accepts_names_and_defaults_to_entra(string? value, AuthMode expected)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Mode"] = value })
            .Build();

        Assert.Equal(expected, AuthModeResolver.Resolve(config, []));
    }

    [Theory]
    [InlineData("1")]     // Enum.TryParse would read this as Local
    [InlineData("Locale")] // a typo must not fall back to Entra
    public void Auth_mode_rejects_anything_else(string value)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Mode"] = value })
            .Build();

        StartupConfigurationException ex = Assert.Throws<StartupConfigurationException>(() => AuthModeResolver.Resolve(config, []));
        Assert.Equal("Auth:Mode must be Entra or Local. Leave it unset for Entra, or start with --local.", ex.Message);
    }
}
