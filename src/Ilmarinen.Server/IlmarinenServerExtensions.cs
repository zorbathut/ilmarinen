using Ilmarinen.Protocol;
using Ilmarinen.Server.Hubs;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUlid;

namespace Ilmarinen.Server;

public static class IlmarinenServerExtensions
{
    public static IServiceCollection AddIlmarinenServer(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new UlidJsonConverter());
        });

        services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new UlidJsonConverter());
            });

        services.AddSignalR()
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.Converters.Add(new UlidJsonConverter());
            });

        services.AddSingleton<CredentialEncryptionService>();
        services.AddSingleton<ServerKeyService>();
        services.AddScoped<WorkerRegistrationService>();
        services.AddScoped<JobRepository>();
        services.AddScoped<JobLogRepository>();
        services.AddSingleton<WorkerRepository>();
        services.AddScoped<ArtifactRepository>();
        services.AddScoped<PipelineRepository>();
        services.AddScoped<SubscriberRepository>();
        services.AddScoped<NotificationRepository>();
        services.AddSingleton<JobScheduler>();
        services.AddSingleton<UIEventService>();
        services.AddSingleton<LogSubscriptionService>();
        services.AddSingleton<LogStreamService>();
        services.AddSingleton<WorkspaceDeletionService>();
        services.AddScoped<DashboardService>();
        services.AddHostedService<PipelineSchedulerService>();
        services.AddHostedService<NotificationCleanupService>();

        return services;
    }

    /// <summary>
    /// Maps endpoints accessible on the public port:
    /// REST API controllers, JobLogsHub for UI log streaming.
    /// </summary>
    public static IEndpointRouteBuilder MapPublicEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapControllers();
        endpoints.MapHub<JobLogsHub>("/hub/job-logs");
        return endpoints;
    }

    /// <summary>
    /// Maps endpoints accessible only on the worker port:
    /// WorkerHub for worker registration and communication,
    /// artifact upload for workers to store build artifacts.
    /// </summary>
    public static IEndpointRouteBuilder MapWorkerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<WorkerHub>("/hub/workers");

        endpoints.MapPost("/hub/workers/jobs/{jobId}/artifacts", async (
            string jobId,
            string name,
            HttpRequest request,
            ArtifactRepository artifacts,
            ILogger<ArtifactRepository> logger) =>
        {
            if (!Ulid.TryParse(jobId, out var jobUlid))
                return Results.BadRequest("Invalid job ID");

            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest("Artifact name is required");

            var contentLength = request.ContentLength ?? 0;

            logger.LogInformation("Receiving artifact {Name} ({Size} bytes) for job {JobId}",
                name, contentLength, jobId);

            var artifact = await artifacts.SaveAsync(
                jobUlid,
                name,
                contentLength,
                request.Body);

            logger.LogInformation("Artifact saved: {Id} ({Name})", artifact.Id, artifact.Name);

            return Results.Ok(artifact);
        }).DisableAntiforgery();

        return endpoints;
    }
}
