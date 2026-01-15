using System.Net;
using System.Net.Sockets;
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
/// SignalR clients can connect via TCP.
/// </summary>
public class IlmarinenWebApplicationFactory : IAsyncDisposable
{
    private readonly TestPostgresContainer _postgres = new();

    private WebApplication? _app;
    private bool _initialized;

    public string PostgresConnectionString => _postgres.ConnectionString;
    public string ServerUrl { get; private set; } = null!;
    public IServiceProvider Services => _app?.Services ?? throw new InvalidOperationException("Server not started");

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        await _postgres.StartAsync();

        var port = GetAvailablePort();
        ServerUrl = $"http://localhost:{port}";

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });

        builder.WebHost.UseUrls(ServerUrl);

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(PostgresConnectionString);
        dataSourceBuilder.EnableDynamicJson();
        var dataSource = dataSourceBuilder.Build();

        builder.Services.AddDbContext<IlmarinenDbContext>(options =>
            options.UseNpgsql(dataSource));

        builder.Services.AddIlmarinenServer();

        builder.Environment.EnvironmentName = "Testing";

        _app = builder.Build();

        _app.MapIlmarinenServer();

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
    }
}
