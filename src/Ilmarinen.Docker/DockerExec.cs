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
        while (true)
        {
            var result = await multiplexed.ReadOutputAsync(buffer, 0, buffer.Length, default);
            if (result.EOF)
            {
                break;
            }

            var chunk = Encoding.UTF8.GetString(buffer, 0, result.Count);
            await onChunk(result.Target == MultiplexedStream.TargetStream.StandardOut, chunk);
        }

        var inspect = await client.Exec.InspectContainerExecAsync(execCreate.ID);
        return (int)inspect.ExitCode;
    }
}
