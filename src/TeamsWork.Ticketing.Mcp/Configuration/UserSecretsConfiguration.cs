using System.Reflection;
using Microsoft.Extensions.Configuration.EnvironmentVariables;

namespace TeamsWork.Ticketing.Mcp.Configuration;

/// <summary>
/// Adds the user-secrets file where the .NET default builders put it in Development: after appsettings.json and
/// before the environment variables and command line, so those override it. Appending it instead would let a stale
/// secrets file silently win over a variable set in an MCP client's configuration.
/// </summary>
internal static class UserSecretsConfiguration
{
    public static void Add(IConfigurationBuilder configuration, Assembly assembly)
    {
        // AddUserSecrets only appends, so build its source separately and insert it in place.
        foreach (IConfigurationSource source in new ConfigurationBuilder().AddUserSecrets(assembly, optional: true).Sources)
        {
            InsertBeforeEnvironmentVariables(configuration, source);
        }
    }

    /// <summary>
    /// Inserts <paramref name="source"/> just before the unprefixed environment-variables source, or appends it when
    /// there is none. Prefixed sources (DOTNET_, ASPNETCORE_) are host settings that come before appsettings.json.
    /// </summary>
    public static void InsertBeforeEnvironmentVariables(IConfigurationBuilder configuration, IConfigurationSource source)
    {
        IList<IConfigurationSource> sources = configuration.Sources;
        for (int i = sources.Count - 1; i >= 0; i--)
        {
            if (sources[i] is EnvironmentVariablesConfigurationSource { Prefix: null or "" })
            {
                sources.Insert(i, source);
                return;
            }
        }

        sources.Add(source);
    }
}
