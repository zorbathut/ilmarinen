using Docker.DotNet.Models;
using Docker.DotNet;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Docker;

/// <summary>
/// The single implementation of the Docker exec dance: create the exec, attach, drain the multiplexed output stream, inspect for the exit code. Callers supply the per-chunk sink.
/// </summary>
internal static class DockerExec
{
    internal static async Task<int> RunAsync(DockerClient client, string containerId, IList<string> cmd, string? workDir, string? user, Func<bool, string, Task> onChunk)
    {
        var execCreate = await client.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
        {
            Cmd = cmd,
            AttachStdout = true,
            AttachStderr = true,
            WorkingDir = workDir,
            User = user
        });

        using var multiplexed = await client.Exec.StartAndAttachContainerExecAsync(execCreate.ID, false);

        var buffer = new byte[4096];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];

        // One decoder per stream, carrying state across reads: a read ends wherever the buffer fills, which can be partway through a multi-byte character.
        var stdoutDecoder = Encoding.UTF8.GetDecoder();
        var stderrDecoder = Encoding.UTF8.GetDecoder();

        while (true)
        {
            var result = await multiplexed.ReadOutputAsync(buffer, 0, buffer.Length, default);
            if (result.EOF)
            {
                break;
            }

            var isStdout = result.Target == MultiplexedStream.TargetStream.StandardOut;
            var decoder = isStdout ? stdoutDecoder : stderrDecoder;
            var count = decoder.GetChars(buffer, 0, result.Count, chars, 0, flush: false);
            if (count > 0)
            {
                await onChunk(isStdout, new string(chars, 0, count));
            }
        }

        // Whatever a stream ended partway through is malformed, and comes out as a replacement character rather than not at all.
        foreach (var (isStdout, decoder) in new[] { (true, stdoutDecoder), (false, stderrDecoder) })
        {
            var count = decoder.GetChars(buffer, 0, 0, chars, 0, flush: true);
            if (count > 0)
            {
                await onChunk(isStdout, new string(chars, 0, count));
            }
        }

        var inspect = await client.Exec.InspectContainerExecAsync(execCreate.ID);
        return (int)inspect.ExitCode;
    }
}
