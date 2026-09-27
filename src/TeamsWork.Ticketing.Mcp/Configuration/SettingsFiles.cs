using Microsoft.Extensions.Configuration.Json;

namespace TeamsWork.Ticketing.Mcp.Configuration;

/// <summary>
/// The appsettings files the host reads from beside the executable. The local modes don't trust them (see
/// Program.UseBuiltInDefaultsInsteadOfFiles), so nothing that decides whether a mode is local may come from them either.
/// </summary>
public static class SettingsFiles
{
    public static bool IsSettingsFile(IConfigurationSource source) =>
        source is JsonConfigurationSource { Path: string path } && path.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The configuration without the settings files, as its own root (built from the same sources, so values set on
    /// the command line, in the environment, or by a test host are kept). The caller disposes it.
    /// </summary>
    public static ConfigurationRoot Without(IConfigurationBuilder configuration)
    {
        ConfigurationBuilder trusted = new();
        foreach (IConfigurationSource source in configuration.Sources.Where(s => !IsSettingsFile(s)))
        {
            trusted.Add(source);
        }

        return (ConfigurationRoot)trusted.Build();
    }
}
