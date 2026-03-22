using Ilmarinen.Database;
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
    "Ilmarinen Server starting - Public: {PublicPort}, Worker: {WorkerPort}",
    serverConfig.PublicPort,
    serverConfig.WorkerPort);

app.Run();

public partial class Program { }
