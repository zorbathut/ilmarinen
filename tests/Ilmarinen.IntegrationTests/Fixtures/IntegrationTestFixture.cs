using Ilmarinen.Database;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Controllers;
using Ilmarinen.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System;

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
    };

    public HttpClient HttpClient => _httpClient;
    public string ServerUrl { get; private set; } = null!;
    public string WorkerUrl { get; private set; } = null!;
    public IServiceProvider Services => _factory.Services;
    public string? WorkerWorkspacePath => _workerBuilder?.WorkspacePath;

    public async Task SetupAsync()
    {
        // Limit concurrent fixtures to avoid exhausting Docker's network address pool.
        // Each fixture may run a job that creates a Docker network.
        await DockerCleanup.NetworkSemaphore.WaitAsync();

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

    private int _workerCount;

    public Task<Guid> StartWorkerAsync() => StartWorkerAsync(diagnostic: null, waitForReady: true);

    public async Task<Guid> StartWorkerAsync(Ilmarinen.Worker.Services.IWorkerDiagnostic? diagnostic, bool waitForReady)
    {
        // Pre-register the worker on the server to get auth credentials
        _workerCount++;
        using var scope = _factory.Services.CreateScope();
        var registrationService = scope.ServiceProvider.GetRequiredService<WorkerRegistrationService>();
        var result = await registrationService.RegisterWorkerAsync($"test-worker-{_workerCount}");

        _workerBuilder = new TestWorkerBuilder(WorkerUrl, result.WorkerKey, diagnostic);
        _workerHost = _workerBuilder.Build();
        _workerCts = new CancellationTokenSource();

        // Start worker in background
        _ = _workerHost.RunAsync(_workerCts.Token);

        if (waitForReady)
        {
            await WaitForWorkerReadyAsync(_workerBuilder.WorkerId);
        }
        else
        {
            await WaitForWorkerConnectionAsync(_workerBuilder.WorkerId);
        }

        return _workerBuilder.WorkerId;
    }

    /// <summary>
    /// Waits until the worker has connected and authenticated (does not require Ready).
    /// Useful for tests of the not-ready path.
    /// </summary>
    public async Task WaitForWorkerConnectionAsync(Guid workerId, int timeoutMs = 30000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            using var scope = _factory.Services.CreateScope();
            var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();
            if (workers.FindConnectionIdByWorkerId(workerId) != null)
                return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Worker {workerId} did not connect within {timeoutMs}ms");
    }

    /// <summary>
    /// Waits until the worker is connected, has run its startup diagnostic, and is Ready.
    /// Default timeout includes time for the diagnostic to pull alpine and run a container.
    /// </summary>
    public async Task WaitForWorkerReadyAsync(Guid workerId, int timeoutMs = 60000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            using var scope = _factory.Services.CreateScope();
            var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

            var view = await workers.GetByIdAsync(workerId);
            if (view != null && view.IsReady)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"Worker {workerId} did not become Ready within {timeoutMs}ms");
    }

    public async Task<Guid> SubmitJobAsync(JobSubmission submission)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/jobs", submission, JsonOptions);
        await EnsureSuccessAsync(response);

        var result = await response.Content.ReadFromJsonAsync<JobSubmissionResult>(JsonOptions);
        return result!.Id;
    }

    public async Task<Guid> RetryJobAsync(Guid jobId)
    {
        var response = await _httpClient.PostAsync($"/api/jobs/{jobId}/retry", null);
        await EnsureSuccessAsync(response);

        var result = await response.Content.ReadFromJsonAsync<JobSubmissionResult>(JsonOptions);
        return result!.Id;
    }

    public async Task<JobInfo> GetJobAsync(Guid jobId)
    {
        var response = await _httpClient.GetAsync($"/api/jobs/{jobId}");
        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<JobInfo>(JsonOptions))!;
    }

    public async Task<JobInfo> WaitForJobCompletionAsync(Guid jobId, int timeoutMs = 120000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            var job = await GetJobAsync(jobId);

            if (job.Status is JobStatus.Success or JobStatus.Failed or JobStatus.Cancelled)
            {
                if (job.Status == JobStatus.Failed)
                {
                    var logs = await GetJobLogsAsync(jobId);
                    Console.WriteLine($"=== JOB LOGS FOR FAILED JOB {jobId} ===");
                    Console.WriteLine(string.IsNullOrEmpty(logs) ? "(no logs)" : logs);
                    Console.WriteLine("=== END JOB LOGS ===");
                }
                return job;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"Job {jobId} did not complete within {timeoutMs}ms");
    }

    public async Task<bool> CancelJobAsync(Guid jobId)
    {
        var response = await _httpClient.DeleteAsync($"/api/jobs/{jobId}");
        return response.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<ArtifactInfo>> GetArtifactsAsync(Guid jobId)
    {
        var response = await _httpClient.GetAsync($"/api/jobs/{jobId}/artifacts");
        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<List<ArtifactInfo>>(JsonOptions))!;
    }

    public async Task<byte[]> DownloadArtifactAsync(Guid jobId, Guid artifactId)
    {
        var response = await _httpClient.GetAsync($"/api/jobs/{jobId}/artifacts/{artifactId}/download");
        await EnsureSuccessAsync(response);

        return await response.Content.ReadAsByteArrayAsync();
    }

    public async Task<RepositoryInfo> CreateRepositoryAsync(RepositorySubmission submission)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/repositories", submission, JsonOptions);
        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<RepositoryInfo>(JsonOptions))!;
    }

    public async Task<RepositoryInfo> GetRepositoryAsync(Guid id)
    {
        var response = await _httpClient.GetAsync($"/api/repositories/{id}");
        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<RepositoryInfo>(JsonOptions))!;
    }

    public async Task<RepositoryInfo> UpdateRepositoryAsync(Guid id, RepositoryUpdate update)
    {
        var response = await _httpClient.PutAsJsonAsync($"/api/repositories/{id}", update, JsonOptions);
        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<RepositoryInfo>(JsonOptions))!;
    }

    public async Task<PipelineInfo> CreatePipelineAsync(PipelineSubmission submission)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/pipelines", submission, JsonOptions);
        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<PipelineInfo>(JsonOptions))!;
    }

    public async Task<PipelineInfo> GetPipelineAsync(Guid id)
    {
        var response = await _httpClient.GetAsync($"/api/pipelines/{id}");
        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<PipelineInfo>(JsonOptions))!;
    }

    public async Task<List<PipelineInfo>> GetAllPipelinesAsync()
    {
        var response = await _httpClient.GetAsync("/api/pipelines");
        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<List<PipelineInfo>>(JsonOptions))!;
    }

    public async Task<Guid> TriggerPipelineAsync(Guid id, PipelineTrigger? trigger = null)
    {
        var response = await _httpClient.PostAsJsonAsync($"/api/pipelines/{id}/trigger", trigger, JsonOptions);
        await EnsureSuccessAsync(response);

        var result = await response.Content.ReadFromJsonAsync<JobSubmissionResult>(JsonOptions);
        return result!.Id;
    }

    public async Task<PipelineInfo> UpdatePipelineAsync(Guid id, PipelineUpdate update)
    {
        var response = await _httpClient.PutAsJsonAsync($"/api/pipelines/{id}", update, JsonOptions);
        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<PipelineInfo>(JsonOptions))!;
    }

    public async Task<bool> DeletePipelineAsync(Guid id)
    {
        var response = await _httpClient.DeleteAsync($"/api/pipelines/{id}");
        return response.IsSuccessStatusCode;
    }

    public async Task<SubscriberInfo> RegisterSubscriberAsync(SubscriberRegistration registration)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/subscribers", registration, JsonOptions);
        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<SubscriberInfo>(JsonOptions))!;
    }

    public async Task<SubscriberInfo> GetSubscriberAsync(Guid id)
    {
        var response = await _httpClient.GetAsync($"/api/subscribers/{id}");
        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<SubscriberInfo>(JsonOptions))!;
    }

    public async Task SubscriberHeartbeatAsync(Guid id)
    {
        var response = await _httpClient.PostAsync($"/api/subscribers/{id}/heartbeat", null);
        await EnsureSuccessAsync(response);
    }

    public async Task<List<JobNotification>> PullNotificationsAsync(Guid subscriberId, int limit = 10)
    {
        var response = await _httpClient.PostAsync(
            $"/api/subscribers/{subscriberId}/notifications?limit={limit}", null);
        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<List<JobNotification>>(JsonOptions))!;
    }

    public async Task AcknowledgeNotificationsAsync(Guid subscriberId, List<Guid> notificationIds)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"/api/subscribers/{subscriberId}/notifications/ack", notificationIds, JsonOptions);
        await EnsureSuccessAsync(response);
    }

    public async Task<List<JobNotification>> WaitForNotificationsAsync(
        Guid subscriberId, int expectedCount = 1, int timeoutMs = 30000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            var notifications = await PullNotificationsAsync(subscriberId);
            if (notifications.Count >= expectedCount)
                return notifications;

            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"Expected {expectedCount} notification(s) for subscriber {subscriberId} within {timeoutMs}ms");
    }

    public async Task StopWorkerAsync(bool preserveIdentity = false)
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

        if (!preserveIdentity)
        {
            _workerBuilder?.Cleanup();
            _workerBuilder = null;
        }
    }

    /// <summary>
    /// Restart a worker with the same identity after StopWorkerAsync(preserveIdentity: true).
    /// </summary>
    public async Task<Guid> RestartWorkerAsync()
    {
        if (_workerBuilder == null)
            throw new InvalidOperationException(
                "No preserved worker identity. Call StopWorkerAsync(preserveIdentity: true) first.");

        _workerHost = _workerBuilder.Build();
        _workerCts = new CancellationTokenSource();
        _ = _workerHost.RunAsync(_workerCts.Token);
        await WaitForWorkerReadyAsync(_workerBuilder.WorkerId);
        return _workerBuilder.WorkerId;
    }

    /// <summary>
    /// Poll until the job reaches the expected status.
    /// </summary>
    public async Task<JobInfo> WaitForJobStatusAsync(Guid jobId, JobStatus expected, int timeoutMs = 30000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            var job = await GetJobAsync(jobId);
            if (job.Status == expected)
                return job;

            await Task.Delay(500);
        }

        var finalJob = await GetJobAsync(jobId);
        throw new TimeoutException(
            $"Job {jobId} did not reach status {expected} within {timeoutMs}ms (current: {finalJob.Status})");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopWorkerAsync();
            _httpClient?.Dispose();
            await _factory.DisposeAsync();
        }
        finally
        {
            DockerCleanup.NetworkSemaphore.Release();
        }
    }

    public async Task<string> GetJobLogsAsync(Guid jobId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IlmarinenDbContext>();
        var chunks = await db.JobLogChunks
            .Where(c => c.JobId == jobId)
            .OrderBy(c => c.SequenceNumber)
            .Select(c => c.Content)
            .ToListAsync();
        return string.Join("", chunks);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.StatusCode}: {body}");
        }
    }
}
