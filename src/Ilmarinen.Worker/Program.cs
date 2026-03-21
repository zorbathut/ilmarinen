using Ilmarinen.Worker;
using Ilmarinen.Worker.Services;
using Serilog;

var serverUrl = GetArg(args, "--server") ?? "http://localhost:8081";
var workerKey = Environment.GetEnvironmentVariable("ILMARINEN_WORKER_KEY")
    ?? throw new InvalidOperationException(
        "ILMARINEN_WORKER_KEY is not set. Register this worker on the server first.");

var config = new WorkerConfig
{
    ServerUrl = serverUrl,
    WorkerKey = workerKey
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
builder.Services.AddHostedService<WorkerService>();

Log.Information("Starting worker {WorkerId}", config.GetWorkerId());
Log.Information("Connecting to server: {ServerUrl}", config.ServerUrl);

var host = builder.Build();
host.Run();

static string? GetArg(string[] args, string name)
{
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
            return args[i + 1];
    }
    return null;
}
