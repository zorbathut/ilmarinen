namespace Ilmarinen.Server;

/// <summary>
/// Middleware that restricts endpoint access based on the connection's local port.
/// Uses HttpContext.Connection.LocalPort which is secure and cannot be spoofed
/// (unlike RequireHost which relies on the HTTP Host header).
/// </summary>
public class PortFilteringMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ServerConfig _config;
    private readonly ILogger<PortFilteringMiddleware> _logger;

    public PortFilteringMiddleware(
        RequestDelegate next,
        ServerConfig config,
        ILogger<PortFilteringMiddleware> logger)
    {
        _next = next;
        _config = config;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var localPort = context.Connection.LocalPort;
        var path = context.Request.Path.Value ?? "";

        // Worker endpoints (/workers) only allowed on worker port
        if (path.StartsWith("/workers", StringComparison.OrdinalIgnoreCase))
        {
            if (localPort != _config.WorkerPort)
            {
                _logger.LogWarning(
                    "Blocked worker endpoint access on public port: {Path} from {RemoteIp}",
                    path, context.Connection.RemoteIpAddress);
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }
        // Public endpoints blocked on worker port
        else if (localPort == _config.WorkerPort)
        {
            _logger.LogWarning(
                "Blocked public endpoint access on worker port: {Path} from {RemoteIp}",
                path, context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await _next(context);
    }
}
