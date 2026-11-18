using Docker.DotNet.Models;
using Docker.DotNet;
using Ilmarinen.Docker;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Core.Tests;

/// <summary>
/// A container on several networks takes its default route from the highest gateway priority, ties broken by network name. Job networks are named `ilmarinen-…`, which sorts before most compose networks; a connection the worker opens mid-job must not leave from the job network, which is deleted when the job ends.
/// </summary>
[TestFixture]
public class WorkerNetworkTests
{
    [Test]
    public async Task AttachAsync_JobNetworkSortsFirst_DefaultRouteStaysOnWorkersNetwork()
    {
        using var client = DockerClientFactory.Create();
        var baseNetwork = $"zz-ilmarinen-test-{Guid.NewGuid():N}";
        var jobNetwork = $"ilmarinen-{Guid.NewGuid():N}";
        var createdNetworks = new List<string>();
        string? containerId = null;

        try
        {
            await CreateNetworkAsync(client, baseNetwork);
            createdNetworks.Add(baseNetwork);
            await CreateNetworkAsync(client, jobNetwork);
            createdNetworks.Add(jobNetwork);

            await client.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = "alpine", Tag = "latest" }, null, new Progress<JSONMessage>());
            var created = await client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = "alpine:latest",
                Cmd = ["sleep", "300"],
                HostConfig = new HostConfig { NetworkMode = baseNetwork, AutoRemove = false }
            });
            containerId = created.ID;
            await client.Containers.StartContainerAsync(containerId, new ContainerStartParameters());

            await WorkerNetwork.AttachAsync(client, jobNetwork, containerId, default);

            var baseGateway = await GatewayOfAsync(client, baseNetwork);
            var jobGateway = await GatewayOfAsync(client, jobNetwork);
            var route = await DefaultRouteAsync(client, containerId);

            Assert.That(route, Does.Contain($"via {baseGateway} "), $"default route should stay on the worker's own network; job network gateway is {jobGateway}");
        }
        finally
        {
            if (containerId != null)
            {
                await client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true });
            }
            createdNetworks.Reverse();
            foreach (var network in createdNetworks)
            {
                await client.Networks.DeleteNetworkAsync(network);
            }
        }
    }

    [TestCase("1.47", false)]
    [TestCase("1.48", true)]
    [TestCase("1.56", true)]
    [TestCase("1.100", true)]
    public void HonoursGatewayPriority_ComparesApiVersionNumerically(string apiVersion, bool expected)
    {
        Assert.That(WorkerNetwork.HonoursGatewayPriority(apiVersion), Is.EqualTo(expected));
    }

    private static async Task CreateNetworkAsync(DockerClient client, string name)
    {
        var labels = new Dictionary<string, string> { ["ilmarinen.test.pid"] = Environment.ProcessId.ToString() };
        var pidNamespaceId = LinuxInterop.GetPidNamespaceId();
        if (pidNamespaceId != null)
        {
            labels["ilmarinen.test.pidns"] = pidNamespaceId;
        }

        await client.Networks.CreateNetworkAsync(new NetworksCreateParameters { Name = name, Driver = "bridge", Labels = labels });
    }

    private static async Task<string> GatewayOfAsync(DockerClient client, string network)
    {
        var inspect = await client.Networks.InspectNetworkAsync(network);
        return inspect.IPAM.Config.Select(c => c.Gateway).Single(g => !string.IsNullOrEmpty(g) && !g.Contains(':'));
    }

    private static async Task<string> DefaultRouteAsync(DockerClient client, string containerId)
    {
        var output = new StringBuilder();
        var exitCode = await DockerExec.RunAsync(client, containerId, ["ip", "-4", "route", "show", "default"], null, null, (_, chunk) =>
        {
            output.Append(chunk);
            return Task.CompletedTask;
        });
        Assert.That(exitCode, Is.Zero, output.ToString());
        return output.ToString();
    }
}
