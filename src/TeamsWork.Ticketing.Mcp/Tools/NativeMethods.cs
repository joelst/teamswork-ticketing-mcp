using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace TeamsWork.Ticketing.Mcp.Tools;

/// <summary>
/// The Windows calls that report where an open handle really points, which .NET has no managed API for. The upload
/// folder uses them to check the file it has open, rather than the path it opened, so a path swapped for a link, an
/// 8.3 alias, or a hard link can't pass for a file inside the folder.
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
