using Ilmarinen.Database;
using Ilmarinen.Server.Hubs;
using Ilmarinen.Server.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
builder.Host.UseSerilog((context, config) =>
    config.ReadFrom.Configuration(context.Configuration));

// Configure Npgsql for JSONB
var dataSourceBuilder = new NpgsqlDataSourceBuilder(
    builder.Configuration.GetConnectionString("DefaultConnection"));
dataSourceBuilder.EnableDynamicJson();
var dataSource = dataSourceBuilder.Build();

builder.Services.AddDbContext<IlmarinenDbContext>(options =>
    options.UseNpgsql(dataSource));

// Add services
builder.Services.AddScoped<JobRepository>();
builder.Services.AddScoped<WorkerRepository>();
builder.Services.AddScoped<JobScheduler>();
builder.Services.AddScoped<DashboardService>();

// Add SignalR
builder.Services.AddSignalR();

// Add controllers
builder.Services.AddControllers();

// Add Blazor
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

// Add health checks
builder.Services.AddHealthChecks()
    .AddDbContextCheck<IlmarinenDbContext>();

var app = builder.Build();

// Run database migrations
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
    await db.Database.MigrateAsync();
}

// Use Serilog request logging
app.UseSerilogRequestLogging();

app.UseStaticFiles();
app.UseRouting();

// Map endpoints
app.MapControllers();
app.MapHub<WorkerHub>("/workers");
app.MapHealthChecks("/health");
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

Log.Information("Ilmarinen Server starting on {Urls}", string.Join(", ", app.Urls));

app.Run();
