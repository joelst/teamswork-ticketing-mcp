using ModelContextProtocol;
using TeamsWork.Ticketing.Mcp.Configuration;
using TeamsWork.Ticketing.Mcp.Ticketing.Models;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// The one folder upload_ticket_files may read. An agent can be steered by text it has read (prompt injection), and a
/// tool that uploads any path it's given would let that text send private files, including the user-secrets file with
/// the API key, to a ticket. So a path must resolve inside the folder, may not pass through a hidden (dot) folder or
/// file, and may not pass through a symbolic link or junction that could lead back out of it.
/// </summary>
public sealed class UploadFolder
{
    private const int MaxFiles = 10;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".bmp"] = "image/bmp", [".pdf"] = "application/pdf", [".txt"] = "text/plain",
        [".log"] = "text/plain", [".csv"] = "text/csv", [".json"] = "application/json", [".xml"] = "application/xml",
        [".zip"] = "application/zip", [".eml"] = "message/rfc822", [".msg"] = "application/vnd.ms-outlook",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    };

    private readonly int _maxBytes;

    private UploadFolder(string root, int maxBytes)
    {
        Root = root;
        _maxBytes = maxBytes;
    }

    /// <summary>Full path of the folder, ending in a directory separator.</summary>
    public string Root { get; }

    /// <summary>
    /// Checks the configured folder at startup: it must exist, must not be a drive root or the user's home folder, and
    /// must not contain the user-secrets file.
    /// </summary>
    public static UploadFolder Create(TicketingOptions options, string? userSecretsPath)
    {
        string configured = options.UploadRoot?.Trim() ?? throw new StartupConfigurationException("Ticketing:UploadRoot is not set.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));

        if (!Directory.Exists(root))
        {
            throw new StartupConfigurationException($"Ticketing:UploadRoot '{root}' does not exist. Create the folder, or unset the setting to turn uploads off.");
        }

        // If the folder is itself a link, check and use where it really is.
        if (new DirectoryInfo(root).ResolveLinkTarget(returnFinalTarget: true) is FileSystemInfo target)
        {
            root = Path.TrimEndingDirectorySeparator(target.FullName);
        }

        string home = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if ((Path.GetPathRoot(root) is string drive && string.Equals(Path.TrimEndingDirectorySeparator(drive), root, PathComparison)) ||
            (home.Length > 0 && string.Equals(home, root, PathComparison)))
        {
            throw new StartupConfigurationException("Ticketing:UploadRoot must be a dedicated folder, not a drive root or your home folder.");
        }

        var folder = new UploadFolder(root + Path.DirectorySeparatorChar, options.MaxUploadBytes);
        if (userSecretsPath is not null && folder.Contains(Path.GetFullPath(userSecretsPath)))
        {
            throw new StartupConfigurationException("Ticketing:UploadRoot contains the user-secrets file, which holds the API key. Choose a folder that doesn't.");
        }

        return folder;
    }

    /// <summary>Reads the files for an upload, after checking every path and the total size.</summary>
    public IReadOnlyList<UploadFile> ReadFiles(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            throw new McpException("'paths' must list at least one file.");
        }

        if (paths.Count > MaxFiles)
        {
            throw new McpException($"At most {MaxFiles} files can be uploaded in one call.");
        }

        var files = new List<FileInfo>();
        long total = 0;
        foreach ((string path, int i) in paths.Select((p, i) => (p, i)))
        {
            FileInfo file = Resolve(ToolValidation.RequireText(path, $"paths[{i}]", 1024), $"paths[{i}]");
            total += file.Length;
            if (total > _maxBytes)
            {
                throw new McpException($"The files add up to more than the upload limit of {_maxBytes / (1024 * 1024.0):0.#} MB (Ticketing:MaxUploadBytes).");
            }

            files.Add(file);
        }

        return files.Select(f => new UploadFile(SafeFileName(f.Name), ContentTypeOf(f.Name), ReadCapped(f))).ToList();
    }

    private byte[] ReadCapped(FileInfo file)
    {
        try
        {
            return ReadCappedCore(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"'{file.Name}' could not be read: it may be open in another program, or you may not have access to it.");
        }
    }

    /// <summary>
    /// Resolves a path (relative paths are taken from the upload folder) and checks it stays inside, with no hidden or
    /// linked segment on the way.
    /// </summary>
    internal FileInfo Resolve(string path, string paramName)
    {
        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(Root, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new McpException($"'{paramName}' is not a valid file path.");
        }

        if (!Contains(full))
        {
            throw new McpException($"'{paramName}' is outside the upload folder. Only files in {Root} can be uploaded; copy the file there first.");
        }

        // Every segment below the root, including the file: no dot names, and no links that could point elsewhere.
        string current = Root.TrimEnd(Path.DirectorySeparatorChar);
        foreach (string segment in full[Root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.StartsWith('.'))
            {
                throw new McpException($"'{paramName}' is in or names a hidden item ('{segment}'), which can't be uploaded.");
            }

            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            {
                throw new McpException($"'{paramName}' goes through a link ('{segment}'), which can't be uploaded.");
            }
        }

        var file = new FileInfo(full);
        return file.Exists
            ? file
            : throw new McpException($"'{paramName}' doesn't exist in the upload folder ({Root}).");
    }

    private bool Contains(string fullPath) =>
        fullPath.Length > Root.Length && fullPath.StartsWith(Root, PathComparison);

    /// <summary>Reads at most the upload limit, so a file that grew after the size check can't exceed it.</summary>
    private byte[] ReadCappedCore(FileInfo file)
    {
        using FileStream stream = file.OpenRead();
        if (stream.Length > _maxBytes)
        {
            throw new McpException($"'{file.Name}' is larger than the upload limit (Ticketing:MaxUploadBytes).");
        }

        byte[] buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static string ContentTypeOf(string fileName) =>
        ContentTypes.GetValueOrDefault(Path.GetExtension(fileName), "application/octet-stream");

    /// <summary>
    /// The name sent in the multipart header: printable ASCII only (anything else becomes '_'), without quotes or
    /// slashes, because it goes into a quoted header value as is.
    /// </summary>
    internal static string SafeFileName(string name)
    {
        string clean = new(name
            .Where(c => !char.IsControl(c) && c is not '"' and not '\\' and not '/')
            .Select(c => c is >= ' ' and <= '~' ? c : '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "attachment" : clean.Trim();
    }
}
