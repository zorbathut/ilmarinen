using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// Runs the git command-line tool on repositories the worker manages, insulated from the host's git setup and from what steps left in the repository.
/// </summary>
public static class GitCli
{
    /// <summary>
    /// Long enough for any local operation on a large repository; a git still running past it is stuck, and the job fails rather than holding the worker.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <exception cref="GitCommandException">Thrown if git fails or times out.</exception>
    public static void Run(string workDir, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // The worker's own environment and the host's git config would otherwise steer every call: GIT_DIR and friends can point it at another repository, and system or user config can forbid file transports, demand fsck, rewrite URLs or install hooks
        foreach (var key in startInfo.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.Ordinal)).ToList())
        {
            startInfo.Environment.Remove(key);
        }

        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        // Steps run as root and may chown the repository, which git otherwise refuses to touch; and whatever hooks or fsmonitor a step configured there must not run inside the worker
        foreach (var setting in new[] { "safe.directory=*", "core.hooksPath=/dev/null", "core.fsmonitor=false", "protocol.file.allow=always" })
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(setting);
        }

        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(workDir);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)!;
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("The worker needs the git command-line tool on its PATH.", ex);
        }

        using (process)
        {
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new GitCommandException($"git {string.Join(' ', args)} was still running after {Timeout.TotalMinutes} minutes and was killed: {stderr.Result.Trim()}");
            }

            // Both streams drain to the end, so the exit is the whole story
            stdout.Wait();
            if (process.ExitCode != 0)
            {
                throw new GitCommandException($"git {string.Join(' ', args)} failed with exit code {process.ExitCode}: {stderr.Result.Trim()}");
            }
        }
    }
}
