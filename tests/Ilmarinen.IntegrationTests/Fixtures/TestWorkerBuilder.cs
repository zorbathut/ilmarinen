using Ilmarinen.Worker.Services;
using Ilmarinen.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO;
using System;

namespace Ilmarinen.IntegrationTests.Fixtures;

public class TestWorkerBuilder
{
    private readonly string _serverUrl;
    private readonly string _workerKey;
    private readonly Guid _workerId;
    private readonly string _workspacePath;
    private readonly IWorkerDiagnostic? _diagnosticOverride;

    public TestWorkerBuilder(string serverUrl, string workerKey)
        : this(serverUrl, workerKey, diagnostic: null) { }

    public TestWorkerBuilder(string serverUrl, string workerKey, IWorkerDiagnostic? diagnostic)
    {
        _serverUrl = serverUrl;
        _workerKey = workerKey;
        _diagnosticOverride = diagnostic;
        // Parse the worker ID from the key (format: {name}:{guid}:{priv}:{pub})
        _workerId = Guid.Parse(workerKey.Split(':')[1]);
        _workspacePath = Path.Combine(Path.GetTempPath(), $"ilmarinen-test-worker-{_workerId}");
    }

    public Guid WorkerId => _workerId;
    public string WorkspacePath => _workspacePath;

    public IHost Build()
    {
        var config = new WorkerConfig
        {
            ServerUrl = _serverUrl,
            WorkspacePath = _workspacePath,
            WorkerKey = _workerKey
        };

        var builder = Host.CreateApplicationBuilder();

        // Quiet logging for tests
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter("Ilmarinen", LogLevel.Information);

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<WorkspaceManager>();
        if (_diagnosticOverride != null)
            builder.Services.AddSingleton(_diagnosticOverride);
        else
            builder.Services.AddSingleton<IWorkerDiagnostic, DockerWorkerDiagnostic>();
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
