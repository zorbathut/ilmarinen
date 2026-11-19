using Docker.DotNet.Models;
using Docker.DotNet;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Worker.Metrics;

/// <summary>A container the sampler may measure, as listing it says.</summary>
public sealed record ContainerSeen(string Id, string? Name);

/// <summary>What inspecting a container adds: its image and when it started, which a first sample's rates are measured from.</summary>
public sealed record ContainerDetails(string? Image, DateTimeOffset? StartedAt);

public interface IContainerSource
{
    /// <summary>The containers on the job's network, if it has one yet, and the worker's own container if it runs in one.</summary>
    Task<IReadOnlyList<ContainerSeen>> ListAsync(string? networkName, CancellationToken cancellationToken);

    /// <summary>Null when the container has gone.</summary>
    Task<ContainerDetails?> InspectAsync(string id, CancellationToken cancellationToken);

    /// <summary>Null when the container has gone or stopped: Docker answers a stopped container with an empty response, which read as figures would be a run of zeros.</summary>
    Task<ContainerStatsResponse?> StatsAsync(string id, CancellationToken cancellationToken);

    /// <summary>How many other jobs' networks are on this Docker host.</summary>
    Task<int> CountCotenantsAsync(string? networkName, CancellationToken cancellationToken);
}

public sealed partial class DockerContainerSource : IContainerSource
{
    private readonly DockerClient _client;
    private readonly string? _workerContainerId;

    [GeneratedRegex("^ilmarinen-[0-9a-f]{32}$")]
    private static partial Regex JobNetwork();

    public DockerContainerSource(DockerClient client, string? workerContainerId)
    {
        _client = client;
        _workerContainerId = workerContainerId;
    }

    public async Task<IReadOnlyList<ContainerSeen>> ListAsync(string? networkName, CancellationToken cancellationToken)
    {
        var seen = new List<ContainerSeen>();
        if (networkName != null)
        {
            try
            {
                var network = await _client.Networks.InspectNetworkAsync(networkName, cancellationToken);
                // While a container is being removed its endpoint can linger keyed "ep-<endpoint id>"; there is no container left to measure.
                seen.AddRange((network.Containers ?? new Dictionary<string, EndpointResource>())
                    .Where(c => !c.Key.StartsWith("ep-", StringComparison.Ordinal))
                    .Select(c => new ContainerSeen(c.Key, c.Value.Name)));
            }
            catch (DockerNetworkNotFoundException)
            {
                // Torn down at the end of the run; the worker's container is still worth measuring.
            }
        }

        if (_workerContainerId != null && !seen.Any(c => c.Id.StartsWith(_workerContainerId, StringComparison.Ordinal) || _workerContainerId.StartsWith(c.Id, StringComparison.Ordinal)))
        {
            seen.Add(new ContainerSeen(_workerContainerId, null));
        }
        return seen;
    }

    public async Task<ContainerDetails?> InspectAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            var container = await _client.Containers.InspectContainerAsync(id, cancellationToken);
            DateTimeOffset? started = DateTimeOffset.TryParse(container.State?.StartedAt, out var at) && at.Year > 1 ? at : null;
            return new ContainerDetails(container.Config?.Image, started);
        }
        catch (DockerContainerNotFoundException)
        {
            return null;
        }
    }

    public async Task<ContainerStatsResponse?> StatsAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            var received = new LastValue<ContainerStatsResponse>();
            await _client.Containers.GetContainerStatsAsync(id, new ContainerStatsParameters { Stream = false, OneShot = true }, received, cancellationToken);
            return received.Value is { } stats && stats.Read != default ? stats : null;
        }
        catch (DockerContainerNotFoundException)
        {
            return null;
        }
    }

    public async Task<int> CountCotenantsAsync(string? networkName, CancellationToken cancellationToken)
    {
        var networks = await _client.Networks.ListNetworksAsync(new NetworksListParameters(), cancellationToken);
        return networks.Count(n => n.Name != networkName && JobNetwork().IsMatch(n.Name));
    }
}

/// <summary>Keeps what it is given, synchronously: Progress&lt;T&gt; would post it to a synchronization context and it could arrive after the call returns.</summary>
internal sealed class LastValue<T> : IProgress<T> where T : class
{
    public T? Value { get; private set; }

    public void Report(T value)
    {
        Value = value;
    }
}
