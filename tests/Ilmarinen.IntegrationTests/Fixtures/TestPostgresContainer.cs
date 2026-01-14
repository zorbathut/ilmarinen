using System.Diagnostics;
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
    private const string PostgresImage = "postgres:16-alpine";

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
        Port = GetAvailablePort();
        var pid = Environment.ProcessId;

        await PullImageIfNeededAsync();

        // Create container with PID label for watchdog-based cleanup
        var response = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = PostgresImage,
            Labels = new Dictionary<string, string>
            {
                ["ilmarinen.test.pid"] = pid.ToString()
            },
            Env =
            [
                $"POSTGRES_DB={Database}",
                $"POSTGRES_USER={Username}",
                $"POSTGRES_PASSWORD={Password}"
            ],
            HostConfig = new HostConfig
            {
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    ["5432/tcp"] = [new() { HostPort = Port.ToString() }]
                },
                AutoRemove = true
            }
        });

        _containerId = response.ID;
        await _client.Containers.StartContainerAsync(_containerId, new ContainerStartParameters());

        // Spawn detached watchdog that kills containers if test process dies.
        // Uses setsid -f to create new session AND fork - this makes the watchdog:
        // 1. Immune to SIGHUP when parent dies
        // 2. Independent of parent's file descriptors (no broken pipe issues)
        // We can't track/kill this process (it's double-forked), but that's fine -
        // on normal dispose the container is already stopped.
        Process.Start(new ProcessStartInfo
        {
            FileName = "setsid",
            Arguments = $"-f /bin/sh -c \"while kill -0 {pid} 2>/dev/null; do sleep 1; done; docker rm -f $(docker ps -aq --filter label=ilmarinen.test.pid={pid}) 2>/dev/null\" </dev/null >/dev/null 2>&1",
            UseShellExecute = false,
            CreateNoWindow = true
        });

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

    private async Task PullImageIfNeededAsync()
    {
        try
        {
            await _client.Images.InspectImageAsync(PostgresImage);
        }
        catch (DockerImageNotFoundException)
        {
            await _client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = PostgresImage },
                null,
                new Progress<JSONMessage>());
        }
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
