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

    // Whether paths differing only in case name the same item depends on the volume (a case-sensitive APFS volume, a
    // Windows folder with case sensitivity turned on), not on the operating system, so neither comparison can be right
    // everywhere. Each check uses the one that fails closed instead: a path is inside the folder only if it matches
    // exactly (a different spelling is refused), and a folder counts as overlapping a protected location if it matches
    // in any case (a possible overlap is refused).
    private const StringComparison InsideComparison = StringComparison.Ordinal;
    private const StringComparison OverlapComparison = StringComparison.OrdinalIgnoreCase;

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

    // Where the folder really is, as the operating system reports the path of an open handle: the Windows final path,
    // or the canonical path on Linux (compared with /proc/self/fd). Null where no such check is available (macOS).
    private readonly string? _realRoot;

    private UploadFolder(string root, int maxBytes, string? realRoot)
    {
        Root = root;
        _maxBytes = maxBytes;
        _realRoot = realRoot;
    }

    /// <summary>
    /// Whether uploads can be offered here: only where the file that is actually open can be identified (Windows, and
    /// Linux with statx). Elsewhere (macOS) the checks would rest on paths alone, so the tool isn't offered at all.
    /// </summary>
    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    /// <summary>Full path of the folder, ending in a directory separator.</summary>
    public string Root { get; }

    /// <summary>
    /// Checks the configured folder at startup. Its real location (links resolved, names as stored) must exist, must not
    /// be a drive root, and must not contain the home folder, the application data or configuration folders (where MCP
    /// client configs can hold the API key), or the user-secrets file.
    /// </summary>
    public static UploadFolder Create(TicketingOptions options, string? userSecretsPath)
    {
        if (!IsSupported)
        {
            throw new StartupConfigurationException(
                "File uploads are available on Windows and Linux only. On this system the server can't check which file an open " +
                "handle refers to, so it can't make sure an upload is the file it checked. Unset Ticketing:UploadRoot.");
        }

        string configured = options.UploadRoot?.Trim() ?? throw new StartupConfigurationException("Ticketing:UploadRoot is not set.");
        string root = Path.TrimEndingDirectorySeparator(Canonical(Path.GetFullPath(configured)));

        if (!Directory.Exists(root))
        {
            throw new StartupConfigurationException($"Ticketing:UploadRoot '{root}' does not exist. Create the folder, or unset the setting to turn uploads off.");
        }

        // Where the folder really is, as the operating system resolves an open handle to it: through subst and mapped
        // drives, administrative shares (\\localhost\C$), and links in any parent. The guards below check both this and
        // the canonical path, so no alias of a protected folder passes.
        string? realRoot = OperatingSystem.IsWindows() ? NativeMethods.DirectoryFinalPath(root)
            : OperatingSystem.IsLinux() ? root
            : null;
        if (OperatingSystem.IsWindows() && realRoot is null)
        {
            throw new StartupConfigurationException($"Ticketing:UploadRoot '{root}' can't be opened to check where it is.");
        }

        // A folder Windows resolves to a network path (a share, including \localhost\C$, or a mapped drive) can't be
        // compared with the local folders the checks below protect, since the same folder has two unrelated spellings. So
        // only a local folder is accepted.
        if (realRoot?.StartsWith(@"\\", StringComparison.Ordinal) == true)
        {
            throw new StartupConfigurationException("Ticketing:UploadRoot must be a folder on a local drive, not a network share or mapped drive.");
        }

        string[] locations = realRoot is null ? [root] : [root, Path.TrimEndingDirectorySeparator(realRoot)];
        foreach (string location in locations.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            CheckNotProtected(location, userSecretsPath);
        }

        return new UploadFolder(root + Path.DirectorySeparatorChar, options.MaxUploadBytes, realRoot is null ? null : Path.TrimEndingDirectorySeparator(realRoot));
    }

    /// <summary>
    /// Refuses a folder that is, contains, or sits inside a place where credentials and client configurations live: a
    /// drive root; a folder containing the home folder, the application data or configuration folders, or the
    /// user-secrets file; a folder inside an application settings folder or a hidden folder in the home folder. The
    /// temporary folder, which Windows keeps inside the local application data folder, is allowed below it, but only in
    /// that normal shape: TEMP pointed at the home folder can't widen the exemption, and the temporary folder itself,
    /// which other programs write to, isn't a dedicated folder.
    /// </summary>
    private static void CheckNotProtected(string root, string? userSecretsPath)
    {
        if (Path.GetPathRoot(root) is string drive && string.Equals(Path.TrimEndingDirectorySeparator(drive), root, OverlapComparison))
        {
            throw new StartupConfigurationException("Ticketing:UploadRoot must be a dedicated folder, not a drive root.");
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Canon(string path) => Path.TrimEndingDirectorySeparator(Canonical(Path.GetFullPath(path)));

        var guarded = new List<(string Path, string What)>
        {
            (home, "your home folder"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "the application data folder"),
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "the local application data folder"),
        };
        if (home.Length > 0)
        {
            guarded.Add((Path.Combine(home, ".config"), "the configuration folder"));
            guarded.Add((Path.Combine(home, ".microsoft"), "the .NET user-secrets folder"));
        }

        if (userSecretsPath is not null)
        {
            guarded.Add((userSecretsPath, "the user-secrets file, which holds the API key"));
        }

        foreach ((string path, string what) in guarded.Where(g => g.Path.Length > 0))
        {
            if (IsWithin(Canon(path), root))
            {
                throw new StartupConfigurationException($"Ticketing:UploadRoot must be a dedicated folder that doesn't contain {what}. Choose a folder of its own.");
            }
        }

        // The exemption is for Windows' own temporary folder, at its fixed place inside the local application data folder,
        // not wherever TEMP or TMP point: those can be set to a settings folder, which would widen the exemption to it.
        string temp = OperatingSystem.IsWindows()
            ? Canon(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"))
            : Canon(Path.GetTempPath());
        if (string.Equals(root, temp, OverlapComparison) || string.Equals(root, Canon(Path.GetTempPath()), OverlapComparison))
        {
            throw new StartupConfigurationException("Ticketing:UploadRoot must be a dedicated folder, not the temporary folder itself. Use a folder inside it.");
        }

        string? canonicalHome = home.Length > 0 ? Canon(home) : null;
        bool TempExempt(string container) =>
            IsWithin(root, temp) &&
            IsWithin(temp, container) && !string.Equals(temp, container, OverlapComparison) &&
            (canonicalHome is null || !HasHiddenSegmentBelow(temp, canonicalHome));

        var settingsFolders = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        if (home.Length > 0)
        {
            settingsFolders.Add(Path.Combine(home, ".config"));
        }

        foreach (string settings in settingsFolders.Where(f => f.Length > 0).Select(Canon))
        {
            if (IsWithin(root, settings) && !TempExempt(settings))
            {
                throw new StartupConfigurationException(
                    "Ticketing:UploadRoot must not be inside an application settings folder (such as AppData or ~/.config), where client " +
                    "configurations and credentials live. Choose a folder of its own.");
            }
        }

        if (canonicalHome is not null && IsWithin(root, canonicalHome) && HasHiddenSegmentBelow(root, canonicalHome) && !TempExempt(canonicalHome))
        {
            throw new StartupConfigurationException("Ticketing:UploadRoot must not be inside a hidden folder (such as ~/.ssh). Choose a folder of its own.");
        }
    }

    /// <summary>Whether any segment of <paramref name="path"/> below <paramref name="folder"/> is hidden (starts with '.').</summary>
    private static bool HasHiddenSegmentBelow(string path, string folder) =>
        IsWithin(path, folder) &&
        path[folder.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Any(segment => segment.StartsWith('.'));

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

        // Every path is checked before any file is read, so a bad path costs no reads.
        List<(string Path, string Param, FileInfo File)> files = paths
            .Select((p, i) => (ToolValidation.RequireText(p, $"paths[{i}]", 1024), $"paths[{i}]"))
            .Select(x => (x.Item1, x.Item2, Resolve(x.Item1, x.Item2)))
            .ToList();

        // Sizes on disk only give an early answer; the limit is enforced on the bytes actually read, since a file can
        // grow between the check and the read.
        if (files.Sum(f => f.File.Length) > _maxBytes)
        {
            throw TooLarge();
        }

        var uploads = new List<UploadFile>();
        long remaining = _maxBytes;
        foreach ((string path, string param, FileInfo file) in files)
        {
            byte[] content = ReadChecked(path, param, file, remaining);
            remaining -= content.Length;
            uploads.Add(new UploadFile(SafeFileName(file.Name), ContentTypeOf(file.Name), content));
        }

        return uploads;
    }

    private McpException TooLarge() =>
        new($"The files add up to more than the upload limit of {_maxBytes / (1024 * 1024.0):0.#} MB (Ticketing:MaxUploadBytes).");

    /// <summary>
    /// Opens the file, checks its path again now that it is open, and reads from that same handle, refusing more than
    /// <paramref name="budget"/> bytes. A path swapped for a link after the first check is caught by the second; the
    /// bytes read always come from the file that was open when the path was last checked.
    /// </summary>
    private byte[] ReadChecked(string path, string param, FileInfo file, long budget)
    {
        try
        {
            // On Linux the kind of file is checked before opening it: opening a FIFO would wait for a writer (hanging the
            // call), and a device isn't a file to upload. Its identity is kept, to confirm the open handle is the same file.
            NativeMethods.UnixFileInfo? before = null;
            if (OperatingSystem.IsLinux())
            {
                before = NativeMethods.LinuxStat(file.FullName);
                if (before is not { IsRegularFile: true })
                {
                    throw new McpException($"'{param}' isn't a regular file (or can't be checked), so it can't be uploaded.");
                }
            }

            using FileStream stream = new(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!string.Equals(Resolve(path, param).FullName, file.FullName, InsideComparison))
            {
                throw new McpException($"'{param}' changed while it was being read. Try again.");
            }

            VerifyOpenFile(stream, param, before);

            using var buffer = new MemoryStream();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > budget)
                {
                    throw TooLarge();
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"'{file.Name}' could not be read: it may be open in another program, or you may not have access to it.");
        }
    }

    /// <summary>
    /// Checks the file that is actually open, not the path used to open it: where the operating system says the handle
    /// points must be inside the folder, with no hidden segment, and the file must be a regular file with a single name,
    /// so a hard link to a file elsewhere is refused. On Linux it must also be the file checked before opening. Anything
    /// that can't be checked is refused rather than passed.
    /// </summary>
    private void VerifyOpenFile(FileStream stream, string param, NativeMethods.UnixFileInfo? before)
    {
        const string HardLink = "has more than one name on disk (a hard link), so it can't be uploaded. Copy the file into the folder instead.";
        string? real = null;
        if (OperatingSystem.IsWindows())
        {
            real = NativeMethods.FinalPath(stream.SafeFileHandle);
            if (NativeMethods.LinkCount(stream.SafeFileHandle) is not 1)
            {
                throw new McpException($"'{param}' {HardLink}");
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            if (NativeMethods.LinuxStat(stream.SafeFileHandle) is not { IsRegularFile: true } opened || before is null || !opened.SameFileAs(before.Value))
            {
                throw new McpException($"'{param}' turned out, once open, not to be the regular file that was checked, so it wasn't read.");
            }

            if (opened.LinkCount != 1)
            {
                throw new McpException($"'{param}' {HardLink}");
            }

            real = File.ResolveLinkTarget($"/proc/self/fd/{stream.SafeFileHandle.DangerousGetHandle()}", returnFinalTarget: false)?.FullName;
        }

        if (_realRoot is null)
        {
            throw new McpException("Uploads can't be verified on this system, so they aren't read.");
        }

        if (real is null || !IsWithin(real, _realRoot, InsideComparison) || string.Equals(real, _realRoot, InsideComparison) ||
            real[_realRoot.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Any(s => s.StartsWith('.')))
        {
            throw new McpException($"'{param}' turned out, once open, not to be a file in the upload folder, so it wasn't read.");
        }
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    private static bool IsWithin(string path, string folder, StringComparison comparison = OverlapComparison) =>
        string.Equals(path, folder, comparison) || path.StartsWith(folder + Path.DirectorySeparatorChar, comparison);

    /// <summary>
    /// Resolves a path (relative paths are taken from the upload folder) to a file inside it. Each segment below the
    /// folder must name an entry exactly as the file system stores it: an 8.3 short name (ENV~1 for .env) or another
    /// spelling finds nothing, so the text that was checked is the item that is read. No segment may be hidden (start
    /// with '.'), be a symbolic link or junction, or use wildcard or alternate-stream syntax.
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

        if (!(full.Length > Root.Length && full.StartsWith(Root, InsideComparison)))
        {
            throw new McpException(
                $"'{paramName}' is outside the upload folder. Only files in {Root} can be uploaded; copy the file there first. " +
                "Give the path relative to that folder (an absolute path must match its spelling exactly, including letter case).");
        }

        string[] segments = full[Root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = new DirectoryInfo(Root);
        FileSystemInfo? entry = null;
        for (int i = 0; i < segments.Length; i++)
        {
            string segment = segments[i];
            if (segment.IndexOfAny(ForbiddenNameChars) >= 0)
            {
                throw new McpException($"'{paramName}' has a character that can't be part of an upload path in '{segment}'.");
            }

            if (segment.StartsWith('.'))
            {
                throw new McpException($"'{paramName}' is in or names a hidden item ('{segment}'), which can't be uploaded.");
            }

            entry = FindEntry(current, segment, ignoreCase: false)
                    ?? throw new McpException($"'{paramName}' doesn't exist in the upload folder ({Root}). Names must match exactly, including letter case.");

            // LinkTarget is set for symbolic links and junctions, the reparse points that redirect a path. The reparse
            // attribute alone isn't a link: OneDrive's cloud files and deduplicated files carry it. Windows hides it on
            // cloud files from most processes, but a host that exposes placeholders would otherwise see every file in a
            // OneDrive folder (where Known Folder Move puts Documents and Desktop) refused.
            if (entry.LinkTarget is not null)
            {
                throw new McpException($"'{paramName}' goes through a link ('{segment}'), which can't be uploaded.");
            }

            bool last = i == segments.Length - 1;
            if (!last)
            {
                current = entry as DirectoryInfo
                          ?? throw new McpException($"'{paramName}' doesn't exist in the upload folder ({Root}).");
            }
        }

        return entry as FileInfo ?? throw new McpException($"'{paramName}' is not a file.");
    }

    // Wildcards would make the directory lookup match other names; ':' reaches an alternate data stream on Windows.
    private static readonly char[] ForbiddenNameChars =
        OperatingSystem.IsWindows() ? ['*', '?', '<', '>', '"', '|', ':'] : ['*', '?', '[', ']', '\\'];

    /// <summary>The entry in <paramref name="directory"/> whose stored name is <paramref name="name"/>, or null.</summary>
    private static FileSystemInfo? FindEntry(DirectoryInfo directory, string name, bool ignoreCase)
    {
        if (name.IndexOfAny(ForbiddenNameChars) >= 0 || !directory.Exists)
        {
            return null;
        }

        var options = new EnumerationOptions
        {
            MatchType = MatchType.Simple,
            MatchCasing = ignoreCase ? MatchCasing.CaseInsensitive : MatchCasing.CaseSensitive,
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
        };

        StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return directory.EnumerateFileSystemInfos(name, options).FirstOrDefault(e => string.Equals(e.Name, name, comparison));
    }

    /// <summary>
    /// The entry <paramref name="segment"/> names, as the file system resolves it. An exact match wins. Otherwise the
    /// stored spelling is recovered only when the written spelling itself resolves (a case-insensitive volume) and one
    /// entry differs from it only in case; on a case-sensitive volume a wrong-case name resolves to nothing, rather
    /// than to whichever sibling (Inbox or INBOX) the directory listing happens to return first.
    /// </summary>
    private static FileSystemInfo? StoredEntry(DirectoryInfo directory, string segment)
    {
        if (FindEntry(directory, segment, ignoreCase: false) is FileSystemInfo exact)
        {
            return exact;
        }

        string written = Path.Combine(directory.FullName, segment);
        if (!Path.Exists(written) || segment.IndexOfAny(ForbiddenNameChars) >= 0)
        {
            return null;
        }

        var options = new EnumerationOptions { MatchType = MatchType.Simple, MatchCasing = MatchCasing.CaseInsensitive, AttributesToSkip = 0, IgnoreInaccessible = true };
        List<FileSystemInfo> candidates = directory.EnumerateFileSystemInfos(segment, options)
            .Where(e => string.Equals(e.Name, segment, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// The path with each existing segment spelled as the file system stores it and any symbolic link or junction
    /// replaced by its target, so a short name, a different spelling, or a linked parent can't disguise where a folder
    /// is. Segments that don't exist are kept as written.
    /// </summary>
    internal static string Canonical(string fullPath) => Canonical(fullPath, depth: 0);

    private const int MaxLinkDepth = 8;

    private static string Canonical(string fullPath, int depth)
    {
        string root = Path.GetPathRoot(fullPath) ?? "";
        string current = root;
        foreach (string segment in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            FileSystemInfo? entry = StoredEntry(new DirectoryInfo(current), segment);
            if (entry is null)
            {
                current = Path.Combine(current, segment);
                continue;
            }

            if (entry.LinkTarget is null)
            {
                current = entry.FullName;
                continue;
            }

            // A link's target is canonicalized in turn, since its own parents may be links or short names. Past the depth
            // limit (a loop, or a deliberately deep chain) the real location is unknown, and a still-linked path could
            // pass the protected-folder checks as an alias, so that is an error rather than a best guess.
            FileSystemInfo? target = null;
            try
            {
                target = depth < MaxLinkDepth ? entry.ResolveLinkTarget(returnFinalTarget: true) : null;
            }
            catch (IOException)
            {
                // A loop the operating system gave up on.
            }

            if (target is null)
            {
                throw new StartupConfigurationException(
                    $"'{fullPath}' goes through links that can't be followed to a real folder (a loop, or more than {MaxLinkDepth} deep).");
            }

            current = Canonical(target.FullName, depth + 1);
        }

        return current;
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
