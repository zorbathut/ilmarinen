using System.Net.Http.Json;
using System.Text.Json;
using Ilmarinen.Database;
using Ilmarinen.Protocol;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Server.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUlid;

namespace Ilmarinen.IntegrationTests.Fixtures;

public class IntegrationTestFixture : IAsyncDisposable
{
    private IlmarinenWebApplicationFactory _factory = null!;
    private HttpClient _httpClient = null!;
    private IHost? _workerHost;
    private TestWorkerBuilder? _workerBuilder;
    private CancellationTokenSource? _workerCts;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new UlidJsonConverter() }
    };

    public HttpClient HttpClient => _httpClient;
    public string ServerUrl { get; private set; } = null!;
    public string WorkerUrl { get; private set; } = null!;
    public IServiceProvider Services => _factory.Services;
    public string? WorkerWorkspacePath => _workerBuilder?.WorkspacePath;

    public async Task SetupAsync()
    {
        _factory = new IlmarinenWebApplicationFactory();
        await _factory.InitializeAsync();

        _httpClient = _factory.CreateClient();
        ServerUrl = _factory.ServerUrl;
        WorkerUrl = _factory.WorkerUrl;

        // Ensure database is migrated
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        await db.Database.MigrateAsync();
    }

    public async Task<Ulid> StartWorkerAsync()
    {
        _workerBuilder = new TestWorkerBuilder(WorkerUrl, ServerUrl);
        _workerHost = _workerBuilder.Build();
        _workerCts = new CancellationTokenSource();

        // Start worker in background
        _ = _workerHost.RunAsync(_workerCts.Token);

        // Wait for worker to register
        await WaitForWorkerRegistrationAsync(_workerBuilder.WorkerId);

        return _workerBuilder.WorkerId;
    }

    private async Task WaitForWorkerRegistrationAsync(Ulid workerId, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();

            var worker = await db.Workers.FirstOrDefaultAsync(w => w.Id == workerId);
            if (worker is { IsConnected: true })
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Worker {workerId} did not register within {timeoutMs}ms");
    }

    public async Task<Ulid> SubmitJobAsync(JobSubmission submission)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/jobs", submission, JsonOptions);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JobSubmissionResult>(JsonOptions);
        return result!.Id;
    }

    public async Task<JobInfo> GetJobAsync(Ulid jobId)
    {
        var response = await _httpClient.GetAsync($"/api/jobs/{jobId}");
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JobInfo>(JsonOptions))!;
    }

    public async Task<JobInfo> WaitForJobCompletionAsync(Ulid jobId, int timeoutMs = 120000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            var job = await GetJobAsync(jobId);

            if (job.Status is JobStatus.Success or JobStatus.Failed or JobStatus.Cancelled)
            {
                return job;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Job {jobId} did not complete within {timeoutMs}ms");
    }

    public async Task<bool> CancelJobAsync(Ulid jobId)
    {
        var response = await _httpClient.DeleteAsync($"/api/jobs/{jobId}");
        return response.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<ArtifactInfo>> GetArtifactsAsync(Ulid jobId)
    {
        var response = await _httpClient.GetAsync($"/api/jobs/{jobId}/artifacts");
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<List<ArtifactInfo>>(JsonOptions))!;
    }

    public async Task<byte[]> DownloadArtifactAsync(Ulid jobId, Ulid artifactId)
    {
        var response = await _httpClient.GetAsync($"/api/jobs/{jobId}/artifacts/{artifactId}/download");
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync();
    }

    public async Task StopWorkerAsync()
    {
        if (_workerCts != null)
        {
            await _workerCts.CancelAsync();
            _workerCts.Dispose();
            _workerCts = null;
        }

        if (_workerHost != null)
        {
            await _workerHost.StopAsync(TimeSpan.FromSeconds(5));
            _workerHost.Dispose();
            _workerHost = null;
        }

        _workerBuilder?.Cleanup();
        _workerBuilder = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopWorkerAsync();
        _httpClient?.Dispose();
        await _factory.DisposeAsync();
    }
}
