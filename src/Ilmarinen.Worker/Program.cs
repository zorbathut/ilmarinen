using Ilmarinen.Protocol;
using Ilmarinen.Worker.Host;
using Ilmarinen.Worker.Presence;
using Ilmarinen.Worker.Services;
using Ilmarinen.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System.Globalization;
using System;

var serverUrl = Environment.GetEnvironmentVariable("ILMARINEN_SERVER_URL")
    ?? throw new InvalidOperationException(
        "ILMARINEN_SERVER_URL is not set.");
var workerKey = Environment.GetEnvironmentVariable("ILMARINEN_WORKER_KEY")
    ?? throw new InvalidOperationException(
        "ILMARINEN_WORKER_KEY is not set. Register this worker on the server first.");

var workspacePath = Environment.GetEnvironmentVariable("ILMARINEN_WORKSPACE_PATH");
var bundleHash = Environment.GetEnvironmentVariable("ILMARINEN_BUNDLE_HASH");
var metricsInterval = Environment.GetEnvironmentVariable("ILMARINEN_METRICS_INTERVAL_SECONDS") is { } metricsSeconds
    ? double.TryParse(metricsSeconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
        ? TimeSpan.FromSeconds(seconds)
        : throw new InvalidOperationException($"ILMARINEN_METRICS_INTERVAL_SECONDS must be a number of seconds, 0 to turn metrics off; got '{metricsSeconds}'.")
    : (TimeSpan?)null;
var inUseIdleTime = Environment.GetEnvironmentVariable("ILMARINEN_IN_USE_IDLE_MINUTES") is { } idleMinutes
    ? double.TryParse(idleMinutes, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) && minutes > 0
        ? TimeSpan.FromMinutes(minutes)
        : throw new InvalidOperationException($"ILMARINEN_IN_USE_IDLE_MINUTES must be a positive number of minutes; got '{idleMinutes}'.")
    : (TimeSpan?)null;

var config = new WorkerConfig
{
    ServerUrl = serverUrl,
    WorkerKey = workerKey,
    WorkspacePath = workspacePath ?? WorkerConfig.GetDefaultWorkspacePath(),
    BundleHash = bundleHash,
    MetricsInterval = metricsInterval ?? WorkerConfig.DefaultMetricsInterval,
    InUseIdleTime = inUseIdleTime ?? WorkerConfig.DefaultInUseIdleTime
};

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = WorkerConfig.ShutdownTimeout);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Services.AddSerilog();

builder.Services.AddSingleton(config);
builder.Services.AddSingleton<WorkspaceManager>();
builder.Services.AddSingleton<IWorkerDiagnostic, DockerWorkerDiagnostic>();
builder.Services.AddSingleton<SleepInhibitor>();
builder.Services.AddSingleton<UpdateSignal>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Logind>(_ => new Logind());
builder.Services.AddSingleton<WorkerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkerService>());
builder.Services.AddSingleton<PresenceService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PresenceService>());
builder.Services.AddHostedService<SleepWatcher>();

Log.Information("Starting worker {WorkerId}, Protocol: {ProtocolHash}", config.GetWorkerId(), ProtocolVersion.Hash);
Log.Debug("Protocol hash input:\n{HashInput}", ProtocolVersion.HashInput);
if (config.BundleHash != null)
{
    Log.Information("Running under launcher, bundle: {BundleHash}", config.BundleHash);
}
Log.Information("Connecting to server: {ServerUrl}", config.ServerUrl);

var host = builder.Build();
// Resolved before Run(): Run disposes the host's service provider on the way out.
var updateSignal = host.Services.GetRequiredService<UpdateSignal>();
host.Run();

return updateSignal.UpdateRequired ? WorkerExitCodes.UpdateRequired : 0;
