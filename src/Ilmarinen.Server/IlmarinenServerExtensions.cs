using Ilmarinen.Protocol;
using Ilmarinen.Server.Hubs;
using Ilmarinen.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Ilmarinen.Server;

public static class IlmarinenServerExtensions
{
    public static IServiceCollection AddIlmarinenServer(this IServiceCollection services)
    {
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
        services.AddScoped<JobRepository>();
        services.AddScoped<JobLogRepository>();
        services.AddScoped<WorkerRepository>();
        services.AddScoped<ArtifactRepository>();
        services.AddSingleton<JobScheduler>();
        services.AddSingleton<LogSubscriptionService>();
        services.AddSingleton<LogStreamService>();

        return services;
    }

    /// <summary>
    /// Maps endpoints accessible on the public port:
    /// REST API controllers, JobLogsHub for UI log streaming.
    /// </summary>
    public static IEndpointRouteBuilder MapPublicEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapControllers();
        endpoints.MapHub<JobLogsHub>("/job-logs");
        return endpoints;
    }

    /// <summary>
    /// Maps endpoints accessible only on the worker port:
    /// WorkerHub for worker registration and communication.
    /// </summary>
    public static IEndpointRouteBuilder MapWorkerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<WorkerHub>("/workers");
        return endpoints;
    }
}
