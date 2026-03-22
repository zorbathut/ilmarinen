using Ilmarinen.DiscordBot;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .Enrich.FromLogContext()
    .CreateLogger();

try
{
    Log.Information("Starting Ilmarinen Discord Bot...");

    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSerilog();
    builder.Services.AddHostedService<DiscordNotificationService>();

    var host = builder.Build();
    await host.RunAsync();

    Log.Information("Ilmarinen Discord Bot stopped gracefully");
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Ilmarinen Discord Bot terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
