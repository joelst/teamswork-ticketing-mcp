using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// The Windows and Linux calls that report what an open handle really is, which .NET has no managed API for. The upload
/// folder uses them to check the file it has open, rather than the path it opened, so a path swapped for a link, an
/// 8.3 alias, a hard link, or a FIFO can't pass for a file inside the folder.
/// </summary>
internal static class NativeMethods
{
    private const uint FileNameNormalized = 0x0;
    private const uint VolumeNameDos = 0x0;
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    /// <summary>The handle's full path as the file system resolves it (links followed, long names), without the \\?\ prefix.</summary>
    [SupportedOSPlatform("windows")]
    public static string? FinalPath(SafeFileHandle handle)
    {
        char[] buffer = new char[1024];
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, FileNameNormalized | VolumeNameDos);
        if (length > buffer.Length)
        {
            buffer = new char[length];
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, FileNameNormalized | VolumeNameDos);
        }

        if (length == 0 || length > buffer.Length)
        {
            return null;
        }

        string path = new(buffer, 0, (int)length);
        return path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
            : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
            : path;
    }

    /// <summary>The number of names (hard links) the open file has, or null if it can't be read.</summary>
    [SupportedOSPlatform("windows")]
    public static uint? LinkCount(SafeFileHandle handle) =>
        GetFileInformationByHandle(handle, out ByHandleFileInformation info) ? info.NumberOfLinks : null;

    /// <summary>The real path of a directory, read through a handle to it.</summary>
    [SupportedOSPlatform("windows")]
    public static string? DirectoryFinalPath(string directory)
    {
        using SafeFileHandle handle = CreateFileW(directory, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        return handle.IsInvalid ? null : FinalPath(handle);
    }

    // ---- Linux ------------------------------------------------------------------------------------------------

    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxBasicStats = 0x7ff;
    private const uint StatxMountId = 0x1000; // Linux 5.8 and later

    // The fields the checks read; a file system may leave any of them out, and then the file can't be verified.
    private const uint StatxRequired = 0x1 /* type */ | 0x4 /* nlink */ | 0x100 /* ino */;
    private const int ONonBlock = 0x800;
    private const int OCloExec = 0x80000;
    private const int StatxSize = 256;
    private const int FileTypeMask = 0xF000;
    private const int RegularFile = 0x8000;

    /// <summary>
    /// What statx reports about a file: whether it is a regular file, how many names it has, and the ID of the mount it
    /// is on (the ID /proc/self/mountinfo lists; null when the kernel doesn't report it).
    /// </summary>
    public readonly record struct UnixFileInfo(bool IsRegularFile, uint LinkCount, ulong? MountId);

    /// <summary>A path itself, without following a final symbolic link. Null if statx isn't available or fails.</summary>
    [SupportedOSPlatform("linux")]
    public static UnixFileInfo? LinuxStat(string path) => Statx(AtFdCwd, path, AtSymlinkNoFollow);

    /// <summary>
    /// Opens a file read-only without waiting and without following a final symbolic link: a FIFO opens at once instead
    /// of waiting for a writer (the handle is then checked and refused), and a link is refused by the kernel, so a path
    /// swapped for either after it was checked can't hang the call. Null if the file can't be opened that way.
    /// </summary>
    [SupportedOSPlatform("linux")]
    public static SafeFileHandle? LinuxOpenForRead(string path)
    {
        // O_NOFOLLOW is the one flag here whose value differs between architectures; on any other the file isn't opened.
        int noFollow = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 or Architecture.X86 => 0x20000,
            Architecture.Arm64 or Architecture.Arm => 0x8000,
            _ => 0,
        };
        if (noFollow == 0)
        {
            return null;
        }

        try
        {
            int fd = LibcOpen(Utf8Z(path), ONonBlock | OCloExec | noFollow); // O_RDONLY is 0
            return fd < 0 ? null : new SafeFileHandle(fd, ownsHandle: true);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The file an open handle refers to. Null if statx isn't available or fails.</summary>
    [SupportedOSPlatform("linux")]
    public static UnixFileInfo? LinuxStat(SafeFileHandle handle)
    {
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            return Statx((int)handle.DangerousGetHandle(), "", AtEmptyPath);
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    // statx's structure has the same layout on every architecture, unlike stat's, which is why it is used here.
    [SupportedOSPlatform("linux")]
    private static UnixFileInfo? Statx(int directory, string path, int flags)
    {
        byte[] buffer = new byte[StatxSize];
        try
        {
            if (LibcStatx(directory, Utf8Z(path), flags, StatxBasicStats | StatxMountId, buffer) != 0)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null; // a C library without statx (before glibc 2.28): treated as unverifiable
        }

        ReadOnlySpan<byte> b = buffer;
        uint mask = BitConverter.ToUInt32(b[0..4]);
        if ((mask & StatxRequired) != StatxRequired)
        {
            return null; // a field left out would read as zero from the empty buffer
        }

        uint links = BitConverter.ToUInt32(b[16..20]);
        ushort mode = BitConverter.ToUInt16(b[28..30]);
        ulong? mountId = (mask & StatxMountId) != 0 ? BitConverter.ToUInt64(b[144..152]) : null;
        return new UnixFileInfo((mode & FileTypeMask) == RegularFile, links, mountId);
    }

    /// <summary>A path as null-terminated UTF-8, the file-name encoding Linux uses.</summary>
    private static byte[] Utf8Z(string path) => System.Text.Encoding.UTF8.GetBytes(path + "\0");

    // open(2) is variadic, but its third argument is read only with O_CREAT or O_TMPFILE, which aren't used.
    [DllImport("libc", EntryPoint = "open", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int LibcOpen(byte[] pathname, int flags);

    [DllImport("libc", EntryPoint = "statx", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int LibcStatx(int dirfd, byte[] pathname, int flags, uint mask, [Out] byte[] statxbuf);

    // ---- Windows ----------------------------------------------------------------------------------------------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
