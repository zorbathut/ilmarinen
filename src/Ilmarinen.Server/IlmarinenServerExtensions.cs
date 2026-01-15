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

        services.AddScoped<JobRepository>();
        services.AddScoped<JobLogRepository>();
        services.AddScoped<WorkerRepository>();
        services.AddSingleton<JobScheduler>();
        services.AddSingleton<LogSubscriptionService>();
        services.AddSingleton<LogStreamService>();

        return services;
    }

    public static IEndpointRouteBuilder MapIlmarinenServer(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapControllers();
        endpoints.MapHub<WorkerHub>("/workers");
        endpoints.MapHub<JobLogsHub>("/job-logs");

        return endpoints;
    }
}
