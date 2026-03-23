using Docker.DotNet;
using Docker.DotNet.Models;
using NUnit.Framework;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Fixtures;

/// <summary>
/// Runs once before all integration tests to clean up stale Docker networks
/// left behind by previous test runs whose processes no longer exist.
/// </summary>
[SetUpFixture]
public class DockerCleanup
{
    [OneTimeSetUp]
    public async Task PruneStaleNetworks()
    {
        using var client = CreateDockerClient();

        var networks = await client.Networks.ListNetworksAsync(new NetworksListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { ["ilmarinen.test.pid"] = true }
            }
        });

        foreach (var network in networks)
        {
            if (!network.Labels.TryGetValue("ilmarinen.test.pid", out var pidStr)
                || !int.TryParse(pidStr, out var pid))
                continue;

            // Check if the owning process is still alive
            try
            {
                System.Diagnostics.Process.GetProcessById(pid);
                continue; // Still running, leave it alone
            }
            catch (ArgumentException)
            {
                // Process is gone — network is stale
            }

            // Only delete if no containers are still attached
            try
            {
                var inspected = await client.Networks.InspectNetworkAsync(network.ID);
                if (inspected.Containers.Count > 0)
                    continue;

                await client.Networks.DeleteNetworkAsync(network.ID);
            }
            catch
            {
                // Best effort — don't let cleanup failures block the test suite
            }
        }
    }

    private static DockerClient CreateDockerClient()
    {
        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");

        if (!string.IsNullOrEmpty(dockerHost))
            return new DockerClientConfiguration(new Uri(dockerHost)).CreateClient();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new DockerClientConfiguration(new Uri("npipe://./pipe/docker_engine")).CreateClient();

        return new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock")).CreateClient();
    }
}
