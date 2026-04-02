using Ilmarinen.Database;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Ilmarinen.Server.Controllers;
using Ilmarinen.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NUlid;
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
        Converters = { new UlidJsonConverter() }
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

    public async Task<Ulid> StartWorkerAsync()
    {
        // Pre-register the worker on the server to get auth credentials
        _workerCount++;
        using var scope = _factory.Services.CreateScope();
        var registrationService = scope.ServiceProvider.GetRequiredService<WorkerRegistrationService>();
        var result = await registrationService.RegisterWorkerAsync($"test-worker-{_workerCount}");

        _workerBuilder = new TestWorkerBuilder(WorkerUrl, result.WorkerKey);
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
            var workers = scope.ServiceProvider.GetRequiredService<WorkerRepository>();

            var connectionId = workers.FindConnectionIdByWorkerId(workerId);
            if (connectionId != null)
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
        await EnsureSuccessAsync(response);

        var result = await response.Content.ReadFromJsonAsync<JobSubmissionResult>(JsonOptions);
        return result!.Id;
    }

    public async Task<JobInfo> GetJobAsync(Ulid jobId)
    {
        var response = await _httpClient.GetAsync($"/api/jobs/{jobId}");
        await EnsureSuccessAsync(response);

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

    public async Task<bool> CancelJobAsync(Ulid jobId)
    {
        var response = await _httpClient.DeleteAsync($"/api/jobs/{jobId}");
        return response.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyList<ArtifactInfo>> GetArtifactsAsync(Ulid jobId)
    {
        var response = await _httpClient.GetAsync($"/api/jobs/{jobId}/artifacts");
        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<List<ArtifactInfo>>(JsonOptions))!;
    }

    public async Task<byte[]> DownloadArtifactAsync(Ulid jobId, Ulid artifactId)
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

    public async Task<RepositoryInfo> GetRepositoryAsync(Ulid id)
    {
        var response = await _httpClient.GetAsync($"/api/repositories/{id}");
        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<RepositoryInfo>(JsonOptions))!;
    }

    public async Task<RepositoryInfo> UpdateRepositoryAsync(Ulid id, RepositoryUpdate update)
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

    public async Task<PipelineInfo> GetPipelineAsync(Ulid id)
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

    public async Task<Ulid> TriggerPipelineAsync(Ulid id, PipelineTrigger? trigger = null)
    {
        var response = await _httpClient.PostAsJsonAsync($"/api/pipelines/{id}/trigger", trigger, JsonOptions);
        await EnsureSuccessAsync(response);

        var result = await response.Content.ReadFromJsonAsync<JobSubmissionResult>(JsonOptions);
        return result!.Id;
    }

    public async Task<PipelineInfo> UpdatePipelineAsync(Ulid id, PipelineUpdate update)
    {
        var response = await _httpClient.PutAsJsonAsync($"/api/pipelines/{id}", update, JsonOptions);
        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<PipelineInfo>(JsonOptions))!;
    }

    public async Task<bool> DeletePipelineAsync(Ulid id)
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

    public async Task<SubscriberInfo> GetSubscriberAsync(Ulid id)
    {
        var response = await _httpClient.GetAsync($"/api/subscribers/{id}");
        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<SubscriberInfo>(JsonOptions))!;
    }

    public async Task SubscriberHeartbeatAsync(Ulid id)
    {
        var response = await _httpClient.PostAsync($"/api/subscribers/{id}/heartbeat", null);
        await EnsureSuccessAsync(response);
    }

    public async Task<List<JobNotification>> PullNotificationsAsync(Ulid subscriberId, int limit = 10)
    {
        var response = await _httpClient.PostAsync(
            $"/api/subscribers/{subscriberId}/notifications?limit={limit}", null);
        await EnsureSuccessAsync(response);

        return (await response.Content.ReadFromJsonAsync<List<JobNotification>>(JsonOptions))!;
    }

    public async Task AcknowledgeNotificationsAsync(Ulid subscriberId, List<Ulid> notificationIds)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"/api/subscribers/{subscriberId}/notifications/ack", notificationIds, JsonOptions);
        await EnsureSuccessAsync(response);
    }

    public async Task<List<JobNotification>> WaitForNotificationsAsync(
        Ulid subscriberId, int expectedCount = 1, int timeoutMs = 30000)
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
    public async Task<Ulid> RestartWorkerAsync()
    {
        if (_workerBuilder == null)
            throw new InvalidOperationException(
                "No preserved worker identity. Call StopWorkerAsync(preserveIdentity: true) first.");

        _workerHost = _workerBuilder.Build();
        _workerCts = new CancellationTokenSource();
        _ = _workerHost.RunAsync(_workerCts.Token);
        await WaitForWorkerRegistrationAsync(_workerBuilder.WorkerId);
        return _workerBuilder.WorkerId;
    }

    /// <summary>
    /// Poll until the job reaches the expected status.
    /// </summary>
    public async Task<JobInfo> WaitForJobStatusAsync(Ulid jobId, JobStatus expected, int timeoutMs = 30000)
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

    public async Task<string> GetJobLogsAsync(Ulid jobId)
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
