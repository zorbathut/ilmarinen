using Ilmarinen.Worker;
using Ilmarinen.Worker.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUlid;

namespace Ilmarinen.IntegrationTests.Fixtures;

public class TestWorkerBuilder
{
    private readonly string _serverUrl;
    private readonly Ulid _workerId;
    private readonly string _workspacePath;

    public TestWorkerBuilder(string serverUrl)
    {
        _serverUrl = serverUrl;
        _workerId = Ulid.NewUlid();
        _workspacePath = Path.Combine(Path.GetTempPath(), $"ilmarinen-test-worker-{_workerId}");
    }

    public Ulid WorkerId => _workerId;
    public string WorkspacePath => _workspacePath;

    public IHost Build()
    {
        var config = new WorkerConfig
        {
            ServerUrl = _serverUrl,
            WorkerId = _workerId,
            WorkspacePath = _workspacePath
        };

        var builder = Host.CreateApplicationBuilder();

        // Quiet logging for tests
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter("Ilmarinen", LogLevel.Information);

        builder.Services.AddSingleton(config);
        builder.Services.AddHostedService<WorkerService>();

        return builder.Build();
    }

    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_workspacePath))
            {
                SetAttributesNormal(new DirectoryInfo(_workspacePath));
                Directory.Delete(_workspacePath, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private static void SetAttributesNormal(DirectoryInfo dir)
    {
        foreach (var subDir in dir.GetDirectories())
        {
            SetAttributesNormal(subDir);
        }

        foreach (var file in dir.GetFiles())
        {
            file.Attributes = FileAttributes.Normal;
        }
    }
}
