using Ilmarinen.Database;
using Ilmarinen.Server;
using Ilmarinen.Server.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, config) =>
    config.ReadFrom.Configuration(context.Configuration));

var dataSourceBuilder = new NpgsqlDataSourceBuilder(
    builder.Configuration.GetConnectionString("DefaultConnection"));
dataSourceBuilder.EnableDynamicJson();
var dataSource = dataSourceBuilder.Build();

builder.Services.AddDbContext<IlmarinenDbContext>(options =>
    options.UseNpgsql(dataSource));

builder.Services.AddIlmarinenServer();
builder.Services.AddScoped<DashboardService>();

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

app.UseSerilogRequestLogging();
app.UseStaticFiles();
app.UseRouting();

app.MapIlmarinenServer();
app.MapHealthChecks("/health");
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

Log.Information("Ilmarinen Server starting on {Urls}", string.Join(", ", app.Urls));

app.Run();

public partial class Program { }
