using Ilmarinen.Database;
using Ilmarinen.Server.Services;
using Ilmarinen.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.IntegrationTests.Fixtures;

/// <summary>
/// Creates a real Kestrel-hosted test server (not in-memory) so that
/// SignalR clients can connect via TCP. Exposes separate ports for
/// public (API/UI) and worker (SignalR hub) traffic.
/// </summary>
public class IlmarinenWebApplicationFactory : IAsyncDisposable
{
    // Ports are picked below Linux's ephemeral range (32768–60999), which port-0 binds (e.g. AgentApiServer) and Docker's published-port allocator draw from; a restarting server re-binds its old ports, and anything drawing from that pool could claim one in the gap. Reserving each port until its factory is disposed keeps the other fixtures in this process off it too.
    private static readonly HashSet<int> PortsReserved = [];

    private readonly TestPostgresContainer _postgres = new();

    private WebApplication? _app;
    private bool _initialized;
    private string? _artifactPath;
    private string? _serverKeyBase64;
    private int _publicPort;
    private int _workerPort;

    /// <summary>Bundle zip served to launcher-run workers. Set before InitializeAsync; null = feature off.</summary>
    public string? WorkerBundlePath { get; set; }

    public string PostgresConnectionString => _postgres.ConnectionString;

    /// <summary>URL for public endpoints (REST API, Blazor UI).</summary>
    public string ServerUrl { get; private set; } = null!;

    /// <summary>URL for worker endpoints (WorkerHub SignalR).</summary>
    public string WorkerUrl { get; private set; } = null!;

    public IServiceProvider Services => _app?.Services ?? throw new InvalidOperationException("Server not started");

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        await _postgres.StartAsync();

        // Generate a test server key for worker authentication. Held in a field and injected explicitly (not via the ambient ILMARINEN_SERVER_KEY env var) because parallel fixtures overwrite the process-wide variable, and a server restart re-resolving it would pick up a foreign key and break auth for already-registered workers.
        using var testKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _serverKeyBase64 = Convert.ToBase64String(testKey.ExportParameters(true).D!);

        _artifactPath = Path.Combine(Path.GetTempPath(), $"ilmarinen-test-artifacts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_artifactPath);

        // Retry with fresh ports on bind failures (another process may hold one)
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                _publicPort = AllocatePort();
                _workerPort = AllocatePort();
                await StartServerAsync();
                break;
            }
            catch (IOException) when (attempt < 5)
            {
                // Port conflict — pick new ports and retry
                if (_app != null)
                {
                    await _app.DisposeAsync();
                    _app = null;
                }
                ReleasePorts();
            }
        }
    }

    /// <summary>
    /// Stop the server process and start a fresh one on the same ports, with the given worker bundle. This reproduces a production server deploy exactly: new process, new bundle hash, every SignalR connection dropped, in-memory worker state gone, database preserved.
    /// </summary>
    public async Task RestartServerAsync(string? newWorkerBundlePath)
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }

        WorkerBundlePath = newWorkerBundlePath;

        // Same ports, so the reconnecting worker finds us — retry with delay rather than fresh ports, accepting the small window where another process grabs them.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await StartServerAsync();
                break;
            }
            catch (IOException) when (attempt < 10)
            {
                if (_app != null)
                {
                    await _app.DisposeAsync();
                    _app = null;
                }
                await Task.Delay(200);
            }
        }
    }

    private async Task StartServerAsync()
    {
        ServerUrl = $"http://localhost:{_publicPort}";
        WorkerUrl = $"http://localhost:{_workerPort}";

        var serverConfig = new ServerConfig
        {
            PublicPort = _publicPort,
            WorkerPort = _workerPort,
            ArtifactStoragePath = _artifactPath!,
            WorkerBundlePath = WorkerBundlePath
        };

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });

        builder.Services.AddSingleton(serverConfig);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenLocalhost(_publicPort);
            options.ListenLocalhost(_workerPort);
        });

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(PostgresConnectionString);
        dataSourceBuilder.EnableDynamicJson();
        var dataSource = dataSourceBuilder.Build();

        builder.Services.AddDbContext<IlmarinenDbContext>(options =>
            options.UseNpgsql(dataSource)
                .ConfigureWarnings(w =>
                    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning)));

        builder.Services.AddIlmarinenServer();
        // Pin the key: AddIlmarinenServer's registration reads the ambient env var, which parallel fixtures race on. Last registration wins.
        builder.Services.AddSingleton(new ServerKeyService(_serverKeyBase64));
        builder.Services.AddHealthChecks();

        builder.Environment.EnvironmentName = "Testing";

        _app = builder.Build();

        _app.UseMiddleware<ExceptionHandlerMiddleware>();
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

    private static int AllocatePort()
    {
        lock (PortsReserved)
        {
            while (true)
            {
                var port = Random.Shared.Next(20000, 32768);
                if (PortsReserved.Add(port))
                {
                    return port;
                }
            }
        }
    }

    private void ReleasePorts()
    {
        lock (PortsReserved)
        {
            PortsReserved.Remove(_publicPort);
            PortsReserved.Remove(_workerPort);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        ReleasePorts();
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
