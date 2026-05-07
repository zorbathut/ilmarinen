using Docker.DotNet;
using System.Runtime.InteropServices;
using System;

namespace Ilmarinen.Docker;

internal static class DockerClientFactory
{
    public static DockerClient Create()
    {
        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");

        if (!string.IsNullOrEmpty(dockerHost))
            return new DockerClientConfiguration(new Uri(dockerHost)).CreateClient();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new DockerClientConfiguration(new Uri("npipe://./pipe/docker_engine")).CreateClient();

        return new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock")).CreateClient();
    }
}
