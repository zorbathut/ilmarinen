using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Ilmarinen.Database;
using Ilmarinen.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ilmarinen.IntegrationTests.Fixtures;

/// <summary>
/// Creates a real Kestrel-hosted test server (not in-memory) so that
/// SignalR clients can connect via TCP. Exposes separate ports for
/// public (API/UI) and worker (SignalR hub) traffic.
/// </summary>
public class IlmarinenWebApplicationFactory : IAsyncDisposable
{
    private readonly TestPostgresContainer _postgres = new();

    private WebApplication? _app;
    private bool _initialized;
    private string? _artifactPath;

    public string PostgresConnectionString => _postgres.ConnectionString;

    /// <summary>URL for public endpoints (REST API, Blazor UI, JobLogsHub).</summary>
    public string ServerUrl { get; private set; } = null!;

    /// <summary>URL for worker endpoints (WorkerHub SignalR).</summary>
    public string WorkerUrl { get; private set; } = null!;

    public IServiceProvider Services => _app?.Services ?? throw new InvalidOperationException("Server not started");

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        await _postgres.StartAsync();

        // Generate a test server key for worker authentication
        using var testKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var serverKeyBase64 = Convert.ToBase64String(testKey.ExportParameters(true).D!);
        Environment.SetEnvironmentVariable("ILMARINEN_SERVER_KEY", serverKeyBase64);

        var publicPort = GetAvailablePort();
        var workerPort = GetAvailablePort();
        ServerUrl = $"http://localhost:{publicPort}";
        WorkerUrl = $"http://localhost:{workerPort}";

        _artifactPath = Path.Combine(Path.GetTempPath(), $"ilmarinen-test-artifacts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_artifactPath);

        var serverConfig = new ServerConfig
        {
            PublicPort = publicPort,
            WorkerPort = workerPort,
            ArtifactStoragePath = _artifactPath
        };

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });

        builder.Services.AddSingleton(serverConfig);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenLocalhost(publicPort);
            options.ListenLocalhost(workerPort);
        });

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(PostgresConnectionString);
        dataSourceBuilder.EnableDynamicJson();
        var dataSource = dataSourceBuilder.Build();

        builder.Services.AddDbContext<IlmarinenDbContext>(options =>
            options.UseNpgsql(dataSource));

        builder.Services.AddIlmarinenServer();
        builder.Services.AddHealthChecks();

        builder.Environment.EnvironmentName = "Testing";

        _app = builder.Build();

        _app.UseMiddleware<PortFilteringMiddleware>();
        _app.MapPublicEndpoints();
        _app.MapWorkerEndpoints();
        _app.MapHealthChecks("/health");

        await _app.StartAsync();
    }

    public HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri(ServerUrl)
        };
        return client;
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        await _postgres.DisposeAsync();

        // Clean up artifact storage
        if (_artifactPath != null && Directory.Exists(_artifactPath))
        {
            try
            {
                Directory.Delete(_artifactPath, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}
