using Docker.DotNet;
using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NUlid;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Worker.Services;

public class WorkerService : BackgroundService
{
    private readonly WorkerConfig _config;
    private readonly Ulid _workerId;
    private readonly WorkspaceManager _workspaceManager;
    private readonly ILogger<WorkerService> _logger;
    private HubConnection? _connection;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningJobs = new();

    public WorkerService(WorkerConfig config, WorkspaceManager workspaceManager, ILogger<WorkerService> logger)
    {
        _config = config;
        _workerId = config.GetWorkerId();
        _workspaceManager = workspaceManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_config.WorkspacePath);

        // Discover Docker environment (host path for bind mounts, container ID for networking)
        var (hostPath, containerId) = await DiscoverDockerEnvironmentAsync();
        _config.HostWorkspacePath = hostPath;
        _config.WorkerContainerId = containerId;

        _connection = new HubConnectionBuilder()
            .WithUrl($"{_config.ServerUrl}/hub/workers")
            .WithAutomaticReconnect()
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
                options.PayloadSerializerOptions.Converters.Add(new UlidJsonConverter());
            })
            .Build();

        _connection.On<JobAssignment>("AssignJob", OnJobAssigned);
        _connection.On<string>("CancelJob", OnCancelJob);
        _connection.On<string>("DeleteWorkspace", OnDeleteWorkspace);

        _connection.Reconnecting += _ =>
        {
            _logger.LogWarning("Connection lost, attempting to reconnect...");
            return Task.CompletedTask;
        };

        _connection.Reconnected += async _ =>
        {
            _logger.LogInformation("Reconnected to server, re-authenticating...");
            await ConnectAndReady();
        };

        await ConnectWithRetryAsync(stoppingToken);
        await ConnectAndReady();

        _logger.LogInformation("Worker {WorkerId} connected and ready", _workerId);

        // Heartbeat loop
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                if (_connection.State == HubConnectionState.Connected)
                {
                    await _connection.SendAsync("Heartbeat", new WorkerHeartbeat
                    {
                        WorkerId = _workerId,
                        IsReady = true
                    }, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Heartbeat failed");
            }
        }
    }

    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Connecting to {ServerUrl}...", _config.ServerUrl);
                await _connection!.StartAsync(ct);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect, retrying in 5 seconds...");
                await Task.Delay(5000, ct);
            }
        }
    }

    private async Task ConnectAndReady()
    {
        // Step 1: Initiate connection with our nonce, get challenge
        var workerNonce = RandomNumberGenerator.GetBytes(32);
        var challenge = await _connection!.InvokeAsync<AuthChallenge>("Connect", new WorkerConnect
        {
            WorkerId = _workerId,
            ProtocolHash = ProtocolVersion.Hash,
            Nonce = workerNonce
        });

        // Step 2: Verify server identity (server signed both nonces)
        using var serverKey = _config.GetServerPublicKey();
        var challengeData = Encoding.UTF8.GetBytes(
            $"{_workerId}:{Convert.ToBase64String(workerNonce)}:{Convert.ToBase64String(challenge.Nonce)}");

        if (!serverKey.VerifyData(challengeData, challenge.ServerSignature, HashAlgorithmName.SHA256))
        {
            _logger.LogError("Server signature verification failed — wrong server or MITM?");
            throw new InvalidOperationException("Server identity verification failed.");
        }

        // Step 3: Sign the same data and authenticate
        using var workerKey = _config.GetWorkerPrivateKey();
        var signature = workerKey.SignData(challengeData, HashAlgorithmName.SHA256);

        await _connection!.SendAsync("Authenticate", new WorkerAuthenticate
        {
            Signature = signature,
            Workspaces = _workspaceManager.DiscoverWorkspaces()
        });

        await _connection!.SendAsync("Ready");
    }

    private Task OnJobAssigned(JobAssignment job)
    {
        // Return immediately so the SignalR dispatch loop stays unblocked
        // (otherwise CancelJob messages can't be delivered while a job is running)
        _ = ExecuteJobAsync(job);
        return Task.CompletedTask;
    }

    private async Task ExecuteJobAsync(JobAssignment job)
    {
        _logger.LogInformation("Received job {JobId}: {RepoUrl} @ {Ref}", job.Id, job.RepoUrl, job.Ref);

        var startTime = DateTime.UtcNow;
        var logCollector = new LogCollector(job.Id, _connection!);
        var jobKey = job.Id.ToString();
        using var cts = new CancellationTokenSource();
        _runningJobs[jobKey] = cts;

        try
        {
            await _connection!.SendAsync("JobStarted", job.Id);

            var runner = new JobRunner(_config, _workspaceManager, job, _connection!, _logger, logCollector);
            var result = await runner.ExecuteAsync(cts.Token);

            var status = cts.IsCancellationRequested ? JobStatus.Cancelled : result.Status;

            result = result with
            {
                Status = status,
                Duration = DateTime.UtcNow - startTime,
                Workspaces = _workspaceManager.DiscoverWorkspaces()
            };

            await _connection!.SendAsync("JobCompleted", job.Id, result);
            _logger.LogInformation("Job {JobId} completed with status {Status}", job.Id, result.Status);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _logger.LogInformation("Job {JobId} was cancelled", job.Id);

            logCollector.WriteStderr("Job cancelled.");
            await logCollector.FlushAsync();

            await _connection!.SendAsync("JobCompleted", job.Id, new JobResult
            {
                Id = job.Id,
                Status = JobStatus.Cancelled,
                Duration = DateTime.UtcNow - startTime,
                Workspaces = _workspaceManager.DiscoverWorkspaces()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} failed with exception", job.Id);

            // Stream exception to server logs
            logCollector.WriteStderr($"Job failed with exception: {ex}");
            await logCollector.FlushAsync();

            await _connection!.SendAsync("JobCompleted", job.Id, new JobResult
            {
                Id = job.Id,
                Status = JobStatus.Failed,
                Duration = DateTime.UtcNow - startTime,
                Workspaces = _workspaceManager.DiscoverWorkspaces()
            });
        }
        finally
        {
            _runningJobs.TryRemove(jobKey, out _);
        }

        // Signal ready for next job
        await _connection!.SendAsync("Ready");
    }

    private void OnCancelJob(string jobId)
    {
        _logger.LogInformation("Received cancel request for job {JobId}", jobId);
        if (_runningJobs.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
        }
        else
        {
            _logger.LogWarning("Cancel requested for job {JobId} but it is not running on this worker", jobId);
        }
    }

    private async Task OnDeleteWorkspace(string name)
    {
        _logger.LogInformation("Received workspace deletion request: {WorkspaceName}", name);
        var result = _workspaceManager.TryDelete(name);
        await _connection!.SendAsync("WorkspaceDeleted", name, result);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_connection != null)
        {
            await _connection.StopAsync(cancellationToken);
            await _connection.DisposeAsync();
        }

        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Discovers the host-side path for the workspace when running inside Docker.
    /// Uses Docker inspect on own container to find the mount source path.
    /// Also captures the worker container ID for Docker-in-Docker networking.
    /// </summary>
    private async Task<(string hostPath, string? containerId)> DiscoverDockerEnvironmentAsync()
    {
        try
        {
            var dockerHost = Environment.GetEnvironmentVariable("DOCKER_HOST");
            var uri = !string.IsNullOrEmpty(dockerHost)
                ? new Uri(dockerHost)
                : new Uri("unix:///var/run/docker.sock");

            var client = new DockerClientConfiguration(uri).CreateClient();

            // Container ID is typically the hostname when running in Docker
            var containerId = System.Net.Dns.GetHostName();

            var inspect = await client.Containers.InspectContainerAsync(containerId);
            var mount = inspect.Mounts?.FirstOrDefault(m =>
                m.Destination == _config.WorkspacePath);

            if (mount?.Source != null)
            {
                _logger.LogInformation(
                    "Discovered host workspace path: {HostPath} -> {ContainerPath}",
                    mount.Source, mount.Destination);
                _logger.LogInformation(
                    "Running in Docker container: {ContainerId}",
                    containerId);
                return (mount.Source, containerId);
            }

            _logger.LogDebug("No matching mount found for {WorkspacePath}", _config.WorkspacePath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not discover host path (not running in Docker?)");
        }

        // Fallback: assume we're not in Docker, paths are the same
        return (_config.WorkspacePath, null);
    }
}
