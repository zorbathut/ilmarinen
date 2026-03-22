using System.IO;
using System.Runtime.InteropServices;
using System;

namespace Ilmarinen.Docker;

/// <summary>
/// P/Invoke helpers for Linux system calls.
/// </summary>
internal static class LinuxInterop
{
    [DllImport("libc", SetLastError = true)]
    private static extern uint getuid();

    [DllImport("libc", SetLastError = true)]
    private static extern uint getgid();

    // stat structure for x86_64 Linux (glibc)
    // We only need fields up to st_gid, but must include padding for correct layout
    [StructLayout(LayoutKind.Sequential)]
    private struct StatBuffer
    {
        public ulong st_dev;      // Device ID
        public ulong st_ino;      // Inode number
        public ulong st_nlink;    // Number of hard links
        public uint st_mode;      // File mode
        public uint st_uid;       // User ID of owner
        public uint st_gid;       // Group ID of owner
        // Remaining fields omitted - we only need up to st_gid
        // The buffer is larger to ensure stat() doesn't overflow
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 100)]
        public byte[] _padding;
    }

    // Use __xstat on glibc - stat() is a macro that calls this
    // Version 1 is _STAT_VER for x86_64
    [DllImport("libc", EntryPoint = "__xstat", SetLastError = true)]
    private static extern int xstat(int version, string path, out StatBuffer buf);

    private const int StatVersion = 1; // _STAT_VER for x86_64

    /// <summary>
    /// Gets the user spec (UID:GID) for the current process on Linux.
    /// Returns null on non-Linux platforms (macOS Docker Desktop handles this automatically).
    /// </summary>
    public static string? GetUserSpec()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        return $"{getuid()}:{getgid()}";
    }

    /// <summary>
    /// Gets the group ID (GID) of the Docker socket on Linux.
    /// Returns null on non-Linux platforms or if the socket doesn't exist.
    /// </summary>
    public static uint? GetDockerSocketGid()
    {
        if (!OperatingSystem.IsLinux())
            return null;

        const string socketPath = "/var/run/docker.sock";
        if (!File.Exists(socketPath))
            return null;

        if (xstat(StatVersion, socketPath, out var buf) == 0)
            return buf.st_gid;

        return null;
    }
}
