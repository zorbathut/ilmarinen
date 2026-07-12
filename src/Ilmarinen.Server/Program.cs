using Ilmarinen.Database;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Services;
using Ilmarinen.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, config) =>
    config.ReadFrom.Configuration(context.Configuration));

// Bind server configuration for dual-port setup
var serverConfig = builder.Configuration.GetSection("Server").Get<ServerConfig>() ?? new ServerConfig();
builder.Services.AddSingleton(serverConfig);

// Configure Kestrel to listen on both public and worker ports
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(serverConfig.PublicPort);
    options.ListenAnyIP(serverConfig.WorkerPort);
});

var dataSourceBuilder = new NpgsqlDataSourceBuilder(
    builder.Configuration.GetConnectionString("DefaultConnection"));
dataSourceBuilder.EnableDynamicJson();
var dataSource = dataSourceBuilder.Build();

builder.Services.AddDbContext<IlmarinenDbContext>(options =>
    options.UseNpgsql(dataSource));

builder.Services.AddIlmarinenServer();

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<IlmarinenDbContext>();

var app = builder.Build();

// Resolve the key eagerly: a malformed ILMARINEN_SERVER_KEY should stop the server here, with the reason, rather than lying dormant in a lazily-constructed singleton until the first request that needs a key.
ServerKeyService serverKey;
try
{
    serverKey = app.Services.GetRequiredService<ServerKeyService>();
}
catch (ConfigurationException ex)
{
    Log.Fatal("{Message}", ex.Message);
    await Log.CloseAndFlushAsync();
    return 1;
}

if (serverKey.IsPlaceholder)
{
    Log.Warning("ILMARINEN_SERVER_KEY is still the placeholder value. The server will start, but worker registration and credential storage stay disabled until it is set to a real key. Generate one with: openssl rand -base64 32");
}
else if (!serverKey.IsEnabled)
{
    Log.Warning("ILMARINEN_SERVER_KEY is not set. The server will start, but worker registration and credential storage stay disabled until it is. Generate a key with: openssl rand -base64 32");
}

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
    await db.Database.MigrateAsync();
}

app.UseMiddleware<ExceptionHandlerMiddleware>();
app.UseSerilogRequestLogging();
app.UseStaticFiles();
app.UseRouting();

// Port filtering middleware - blocks endpoints based on connection's local port
app.UseMiddleware<PortFilteringMiddleware>();

app.MapPublicEndpoints();
app.MapWorkerEndpoints();
app.MapHealthChecks("/health");
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

Log.Information(
    "Ilmarinen Server starting - Public: {PublicPort}, Worker: {WorkerPort}, Protocol: {ProtocolHash}",
    serverConfig.PublicPort,
    serverConfig.WorkerPort,
    ProtocolVersion.Hash);
Log.Debug("Protocol hash input:\n{HashInput}", ProtocolVersion.HashInput);

app.Run();

return 0;

public partial class Program { }
