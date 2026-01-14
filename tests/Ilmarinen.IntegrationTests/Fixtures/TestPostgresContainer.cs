using System.Net.Sockets;
using System.Runtime.InteropServices;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Ilmarinen.IntegrationTests.Fixtures;

/// <summary>
/// Simple PostgreSQL container helper using Docker.DotNet directly.
/// </summary>
public class TestPostgresContainer : IAsyncDisposable
{
    private readonly DockerClient _client;
    private string? _containerId;

    public string Database { get; }
    public string Username { get; }
    public string Password { get; }
    public int Port { get; private set; }

    public string ConnectionString =>
        $"Host=localhost;Port={Port};Database={Database};Username={Username};Password={Password}";

    public TestPostgresContainer(
        string database = "ilmarinen_test",
        string username = "test",
        string password = "test")
    {
        Database = database;
        Username = username;
        Password = password;
        _client = CreateDockerClient();
    }

    public async Task StartAsync()
    {
        // Find available port
        Port = GetAvailablePort();

        // Create container
        var response = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = "postgres:16-alpine",
            Env = new List<string>
            {
                $"POSTGRES_DB={Database}",
                $"POSTGRES_USER={Username}",
                $"POSTGRES_PASSWORD={Password}"
            },
            HostConfig = new HostConfig
            {
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    ["5432/tcp"] = new List<PortBinding>
                    {
                        new() { HostPort = Port.ToString() }
                    }
                },
                AutoRemove = true
            }
        });

        _containerId = response.ID;

        // Start container
        await _client.Containers.StartContainerAsync(_containerId, new ContainerStartParameters());

        // Wait for PostgreSQL to be ready
        await WaitForReadyAsync();
    }

    private async Task WaitForReadyAsync()
    {
        var timeout = TimeSpan.FromSeconds(30);
        var start = DateTime.UtcNow;

        while (DateTime.UtcNow - start < timeout)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync("localhost", Port);

                // Port is open, but PostgreSQL might not be ready yet
                // Try a simple connection
                await using var conn = new Npgsql.NpgsqlConnection(ConnectionString);
                await conn.OpenAsync();
                return;
            }
            catch
            {
                await Task.Delay(100);
            }
        }

        throw new TimeoutException($"PostgreSQL container did not become ready within {timeout}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_containerId != null)
        {
            try
            {
                await _client.Containers.StopContainerAsync(_containerId, new ContainerStopParameters());
            }
            catch
            {
                // Container might already be stopped/removed (AutoRemove=true)
            }
        }
        _client.Dispose();
    }

    private static DockerClient CreateDockerClient()
    {
        var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");

        if (!string.IsNullOrEmpty(dockerHost))
        {
            return new DockerClientConfiguration(new Uri(dockerHost)).CreateClient();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new DockerClientConfiguration(new Uri("npipe://./pipe/docker_engine")).CreateClient();
        }

        return new DockerClientConfiguration(new Uri("unix:///var/run/docker.sock")).CreateClient();
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
