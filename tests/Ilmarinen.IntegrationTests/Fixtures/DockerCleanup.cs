using Docker.DotNet;
using Docker.DotNet.Models;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Runs once before all integration tests to clean up stale Docker networks
/// left behind by previous test runs whose processes no longer exist.
/// Must be in the global namespace so NUnit treats it as an assembly-level SetUpFixture.
/// </summary>
[SetUpFixture]
public class DockerCleanup
{
    /// <summary>
    /// Limits concurrent Docker network creation to avoid exhausting Docker's address pool.
    /// Capacity is computed from Docker's configured address pools minus existing networks.
    /// Acquire before creating a pipeline network; release after the network is removed.
    /// </summary>
    public static SemaphoreSlim NetworkSemaphore { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task PruneStaleNetworksAndComputeCapacity()
    {
        using var client = CreateDockerClient();

        await PruneStaleNetworksAsync(client);

        var capacity = await ComputeAvailableNetworkCapacityAsync(client);
        NetworkSemaphore = new SemaphoreSlim(capacity, capacity);

        TestContext.Progress.WriteLine(
            $"Docker network semaphore initialized with capacity {capacity}");
    }

    private static async Task PruneStaleNetworksAsync(DockerClient client)
    {
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

            // Owning process is dead — stop orphaned containers and remove the network
            try
            {
                var inspected = await client.Networks.InspectNetworkAsync(network.ID);

                foreach (var container in inspected.Containers)
                {
                    await client.Containers.StopContainerAsync(container.Key,
                        new ContainerStopParameters { WaitBeforeKillSeconds = 1 });
                    await client.Networks.DisconnectNetworkAsync(network.ID,
                        new NetworkDisconnectParameters { Container = container.Key, Force = true });
                }

                await client.Networks.DeleteNetworkAsync(network.ID);
            }
            catch
            {
                // Best effort — don't let cleanup failures block the test suite
            }
        }
    }

    /// <summary>
    /// Computes how many Docker bridge networks we can safely create concurrently.
    /// Reads Docker's default address pools and subtracts existing networks.
    /// </summary>
    private static async Task<int> ComputeAvailableNetworkCapacityAsync(DockerClient client)
    {
        // Docker's built-in default is ~31 bridge networks (172.17.0.0/12 with /16 subnets).
        const int fallbackPoolSize = 31;

        int totalSubnets = fallbackPoolSize;

        var info = await client.System.GetSystemInfoAsync();
        var pools = info.DefaultAddressPools;

        if (pools != null && pools.Count > 0)
        {
            // Each pool has a Base (e.g. "10.42.0.0/16") and Size (e.g. 26). Total subnets per pool = 2^(Size - BasePrefix).
            totalSubnets = 0;
            foreach (var pool in pools)
            {
                var slashIndex = pool.Base.IndexOf('/');
                if (slashIndex < 0 || !int.TryParse(pool.Base[(slashIndex + 1)..], out var basePrefix))
                    continue;

                if (pool.Size <= basePrefix)
                    continue;

                totalSubnets += 1 << (int)(pool.Size - basePrefix);
            }

            if (totalSubnets == 0)
                totalSubnets = fallbackPoolSize;
        }

        // Subtract networks that already exist (non-test system networks we can't touch)
        var existingNetworks = await client.Networks.ListNetworksAsync();
        var nonTestNetworks = existingNetworks.Count(n =>
            !n.Labels.ContainsKey("ilmarinen.test.pid"));

        // Reserve headroom: leave 25% of capacity or at least 4 networks for other Docker usage
        var reserved = Math.Max(4, totalSubnets / 4);
        var available = totalSubnets - nonTestNetworks - reserved;

        return Math.Max(1, Math.Min(available, Environment.ProcessorCount));
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
