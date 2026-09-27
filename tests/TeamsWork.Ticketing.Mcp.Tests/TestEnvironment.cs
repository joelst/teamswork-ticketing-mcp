using System.Collections;
using System.Runtime.CompilerServices;

namespace TeamsWork.Ticketing.Mcp.Tests;

/// <summary>
/// The tests run the server in this process (and start it as a child process, which inherits this environment), and
/// the server reads environment variables and the user-secrets file. Settings on a developer's machine, such as the
/// Ticketing:Region an installer saves or a Ticketing__* variable, would then change what the tests test. So every run
/// starts without them: the server's own variables are cleared, and the user-secrets file is looked for in an empty
/// folder (always the same one, left empty). The user-secrets path helper reads APPDATA before anything else on every
/// OS, so that alone moves it; HOME is left alone, since the upload-folder checks compare folders with it.
/// </summary>
internal static class TestEnvironment
{
    private static readonly string[] Prefixes = ["Ticketing__", "Entra__", "Mcp__", "Auth__", "Local__", "KeyVault__"];

    private static readonly string[] Names = ["MCP_TRANSPORT", "CONTAINER_APP_NAME"];

#pragma warning disable CA2255 // A test assembly is the case the attribute is for: it must run before any test does.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Isolate()
    {
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            string name = (string)variable.Key;
            if (Prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
                Names.Any(n => string.Equals(name, n, StringComparison.OrdinalIgnoreCase)))
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        string appData = Path.Combine(Path.GetTempPath(), "taas-mcp-tests-appdata");
        Directory.CreateDirectory(appData);
        Environment.SetEnvironmentVariable("APPDATA", appData);
    }
}
