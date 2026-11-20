using System;

namespace Ilmarinen.Server;

public class ServerConfig
{
    public int PublicPort { get; set; } = 8080;
    public int WorkerPort { get; set; } = 8081;
    public string ArtifactStoragePath { get; set; } = "/data/artifacts";

    /// <summary>
    /// Path to the zipped worker publish output served to launcher-run workers. Null disables bundle serving.
    /// </summary>
    public string? WorkerBundlePath { get; set; }

    /// <summary>
    /// Where to send wake-on-LAN packets, as address:port (the port defaults to 9): the LAN's broadcast address, which the packet has to be able to reach from wherever the server runs. Null turns waking workers off. Set via Server__WakeAddress.
    /// </summary>
    public string? WakeAddress { get; set; }

    /// <summary>How long a woken worker has to come back ready before the wake is given up on. Set via Server__WakeTimeout (hh:mm:ss).</summary>
    public TimeSpan WakeTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a worker that didn't wake is left alone before it is tried again. Set via Server__WakeBackoff (hh:mm:ss).</summary>
    public TimeSpan WakeBackoff { get; set; } = TimeSpan.FromHours(1);
}
