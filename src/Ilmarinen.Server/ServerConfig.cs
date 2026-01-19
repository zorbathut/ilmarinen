namespace Ilmarinen.Server;

public class ServerConfig
{
    public int PublicPort { get; set; } = 8080;
    public int WorkerPort { get; set; } = 8081;
    public string ArtifactStoragePath { get; set; } = "/data/artifacts";
}
