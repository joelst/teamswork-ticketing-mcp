using Microsoft.Extensions.Configuration;
using TeamsWork.Ticketing.Mcp.Configuration;

namespace TeamsWork.Ticketing.Mcp.Tests;

/// <summary>How the command line reaches the host's configuration.</summary>
public sealed class CommandLineTests
{
    [Theory]
    [InlineData("--stdio")]
    [InlineData("--local")]
    [InlineData("--STDIO")]
    public void A_setting_after_a_mode_flag_is_applied(string flag)
    {
        IConfiguration config = new ConfigurationBuilder()
            .AddCommandLine(ModeFlags.SettingsArguments([flag, "--Ticketing:DefaultTimeZoneId=America/New_York"]))
            .Build();

        Assert.Equal("America/New_York", config["Ticketing:DefaultTimeZoneId"]);
    }

    [Fact]
    public void Without_the_filter_the_flag_swallows_the_setting()
    {
        // What the command-line provider does on its own, and why the flags are removed before it runs.
        IConfiguration config = new ConfigurationBuilder()
            .AddCommandLine(["--stdio", "--Ticketing:DefaultTimeZoneId=America/New_York"])
            .Build();

        Assert.Null(config["Ticketing:DefaultTimeZoneId"]);
    }

    [Fact]
    public void Only_the_mode_flags_are_removed()
    {
        Assert.Equal(["--Local:Port=6123", "--stdiox"], ModeFlags.SettingsArguments(["--local", "--Local:Port=6123", "--stdiox"]));
    }
}
