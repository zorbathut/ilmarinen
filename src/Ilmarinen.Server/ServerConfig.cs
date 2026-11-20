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
}
