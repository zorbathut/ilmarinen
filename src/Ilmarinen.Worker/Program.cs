using Ilmarinen.Protocol;
using Ilmarinen.Worker.Services;
using Ilmarinen.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System;

var serverUrl = Environment.GetEnvironmentVariable("ILMARINEN_SERVER_URL")
    ?? throw new InvalidOperationException(
        "ILMARINEN_SERVER_URL is not set.");
var workerKey = Environment.GetEnvironmentVariable("ILMARINEN_WORKER_KEY")
    ?? throw new InvalidOperationException(
        "ILMARINEN_WORKER_KEY is not set. Register this worker on the server first.");

var workspacePath = Environment.GetEnvironmentVariable("ILMARINEN_WORKSPACE_PATH");
var bundleHash = Environment.GetEnvironmentVariable("ILMARINEN_BUNDLE_HASH");

var config = new WorkerConfig
{
    ServerUrl = serverUrl,
    WorkerKey = workerKey,
    WorkspacePath = workspacePath ?? WorkerConfig.GetDefaultWorkspacePath(),
    BundleHash = bundleHash
};

var builder = Host.CreateApplicationBuilder(args);

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
builder.Services.AddHostedService<WorkerService>();

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
