using System.Net;
using System.Net.Sockets;
using Ilmarinen.Database;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Hubs;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ilmarinen.IntegrationTests.Fixtures;

/// <summary>
/// Creates a real Kestrel-hosted test server (not in-memory) so that
/// SignalR clients can connect via TCP.
/// </summary>
public class IlmarinenWebApplicationFactory : IAsyncDisposable
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("ilmarinen_test")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    private WebApplication? _app;
    private bool _initialized;

    public string PostgresConnectionString => _postgres.GetConnectionString();
    public string ServerUrl { get; private set; } = null!;
    public IServiceProvider Services => _app?.Services ?? throw new InvalidOperationException("Server not started");

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        await _postgres.StartAsync();

        // Find an available port
        var port = GetAvailablePort();
        ServerUrl = $"http://localhost:{port}";

        // Build and configure the app
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.GetName().Name,
        });

        // Configure to use our port
        builder.WebHost.UseUrls(ServerUrl);

        // Configure services - replace database with test container
        builder.Services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new UlidJsonConverter());
            });

        builder.Services.AddSignalR()
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.Converters.Add(new UlidJsonConverter());
            });

        // Remove default DB and use test container
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(PostgresConnectionString);
        dataSourceBuilder.EnableDynamicJson();
        var dataSource = dataSourceBuilder.Build();

        builder.Services.AddDbContext<IlmarinenDbContext>(options =>
            options.UseNpgsql(dataSource));

        // Register server services
        builder.Services.AddScoped<JobRepository>();
        builder.Services.AddScoped<WorkerRepository>();
        builder.Services.AddSingleton<JobScheduler>();

        builder.Environment.EnvironmentName = "Testing";

        _app = builder.Build();

        // Configure middleware
        _app.MapControllers();
        _app.MapHub<WorkerHub>("/workers");

        // Start the server
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
