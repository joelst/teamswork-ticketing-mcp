using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;
using TeamsWork.Ticketing.Mcp.Configuration;

namespace TeamsWork.Ticketing.Mcp.Tests;

public sealed class UserSecretsConfigurationTests
{
    // Unique to this class, so no other test or developer setting can collide with it.
    private const string Key = "TwMcpUserSecretsTest:Value";
    private const string EnvironmentName = "TwMcpUserSecretsTest__Value";

    private static MemoryConfigurationSource Values(string value) =>
        new() { InitialData = [new KeyValuePair<string, string?>(Key, value)] };

    // The shape the default builders produce: host variables (prefixed), appsettings.json, then variables and the
    // command line.
    private static ConfigurationBuilder DefaultShape()
    {
        var builder = new ConfigurationBuilder();
        builder.AddEnvironmentVariables("DOTNET_");
        builder.Add(Values("appsettings"));
        builder.AddEnvironmentVariables();
        builder.AddCommandLine([]);
        return builder;
    }

    [Fact]
    public void Environment_variables_override_the_secrets_file()
    {
        ConfigurationBuilder builder = DefaultShape();
        UserSecretsConfiguration.InsertBeforeEnvironmentVariables(builder, Values("secrets"));

        Environment.SetEnvironmentVariable(EnvironmentName, "environment");
        try
        {
            Assert.Equal("environment", builder.Build()[Key]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvironmentName, null);
        }
    }

    [Fact]
    public void The_secrets_file_overrides_appsettings()
    {
        ConfigurationBuilder builder = DefaultShape();
        var secrets = Values("secrets");
        UserSecretsConfiguration.InsertBeforeEnvironmentVariables(builder, secrets);

        Assert.Equal("secrets", builder.Build()[Key]);
        // After the prefixed host variables and appsettings.json, before the unprefixed variables.
        int index = builder.Sources.IndexOf(secrets);
        Assert.Equal(2, index);
        Assert.IsType<EnvironmentVariablesConfigurationSource>(builder.Sources[index + 1]);
    }

    [Fact]
    public void Appends_when_there_are_no_environment_variables()
    {
        var builder = new ConfigurationBuilder();
        builder.Add(Values("appsettings"));
        var secrets = Values("secrets");

        UserSecretsConfiguration.InsertBeforeEnvironmentVariables(builder, secrets);

        Assert.Same(secrets, builder.Sources[^1]);
    }
}
