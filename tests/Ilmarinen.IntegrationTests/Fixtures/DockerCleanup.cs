using Docker.DotNet;
using Docker.DotNet.Models;
using Ilmarinen.Docker;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        var ownPidNamespace = LinuxInterop.GetPidNamespaceId();
        if (ownPidNamespace == null)
        {
            TestContext.Progress.WriteLine("Skipping stale Docker network cleanup: can't identify this process's PID namespace, so no network owner's PID can be checked");
            return;
        }

        var networks = await client.Networks.ListNetworksAsync(new NetworksListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { ["ilmarinen.test.pid"] = true }
            }
        });

        foreach (var network in networks)
        {
            if (!IsStaleNetwork(network.Labels, ownPidNamespace, IsProcessAlive))
            {
                continue;
            }

            // Owning process is dead — stop the containers it ran on the network, detach everything, and remove the network. A container that merely connected to the network, like a worker running in a container, is only detached: its life isn't the dead run's.
            try
            {
                var inspected = await client.Networks.InspectNetworkAsync(network.ID);

                foreach (var container in inspected.Containers)
                {
                    var details = await client.Containers.InspectContainerAsync(container.Key);
                    if (details.HostConfig.NetworkMode == network.Name)
                    {
                        await client.Containers.StopContainerAsync(container.Key,
                            new ContainerStopParameters { WaitBeforeKillSeconds = 1 });
                    }
                    await client.Networks.DisconnectNetworkAsync(network.ID,
                        new NetworkDisconnectParameters { Container = container.Key, Force = true });
                }

                await client.Networks.DeleteNetworkAsync(network.ID);
            }
            catch (Exception ex)
            {
                // Best effort — don't let cleanup failures block the test suite
                TestContext.Progress.WriteLine($"Could not clean up stale Docker network {network.Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Whether a network was left behind by a process in our own PID namespace that has since died. A PID from any other namespace, such as a worker running in a container, can't be checked from here: it would read as dead while its run is still going.
    /// </summary>
    internal static bool IsStaleNetwork(IDictionary<string, string> labels, string ownPidNamespace, Func<int, bool> isProcessAlive)
    {
        return labels.TryGetValue("ilmarinen.test.pid", out var pidText)
            && int.TryParse(pidText, out var pid)
            && labels.TryGetValue("ilmarinen.test.pidns", out var pidNamespace)
            && pidNamespace == ownPidNamespace
            && !isProcessAlive(pid);
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
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

        // Subtract networks that already exist and that no test run of ours will remove: everything but our own namespace's test networks
        var ownPidNamespace = LinuxInterop.GetPidNamespaceId();
        var existingNetworks = await client.Networks.ListNetworksAsync();
        var nonTestNetworks = existingNetworks.Count(n =>
            !n.Labels.TryGetValue("ilmarinen.test.pidns", out var pidNamespace) || pidNamespace != ownPidNamespace);

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
