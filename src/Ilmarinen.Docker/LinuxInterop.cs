using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System;

namespace Ilmarinen.Docker;

/// <summary>
/// Helpers for Linux-specific process and file identity.
/// </summary>
internal static class LinuxInterop
{
    [DllImport("libc", SetLastError = true)]
    private static extern uint getuid();

    [DllImport("libc", SetLastError = true)]
    private static extern uint getgid();

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

        // Shelling out to stat avoids P/Invoking the libc stat family, whose symbol names and struct layouts vary by libc and architecture. Both coreutils and busybox stat support -c %g.
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "stat",
                ArgumentList = { "-c", "%g", socketPath },
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process == null)
            {
                Console.Error.WriteLine("Warning: could not start `stat` to determine the Docker socket GID; nested containers may lack socket access.");
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            var stderr = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit();

            if (process.ExitCode == 0 && uint.TryParse(output, out var gid))
            {
                return gid;
            }

            Console.Error.WriteLine($"Warning: `stat -c %g {socketPath}` failed (exit {process.ExitCode}{(stderr.Length > 0 ? $": {stderr}" : "")}); nested containers may lack socket access.");
            return null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: could not determine the Docker socket GID ({ex.Message}); nested containers may lack socket access.");
            return null;
        }
    }
}
