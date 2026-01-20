using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Ilmarinen.Server;

/// <summary>
/// Global exception handler that returns ProblemDetails responses (RFC 7807).
/// </summary>
public class ExceptionHandlerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<ExceptionHandlerMiddleware> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ExceptionHandlerMiddleware(
        RequestDelegate next,
        IHostEnvironment environment,
        ILogger<ExceptionHandlerMiddleware> logger)
    {
        _next = next;
        _environment = environment;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (ConfigurationException ex)
        {
            _logger.LogWarning(ex, "Configuration error: {Message}", ex.Message);
            await WriteProblemDetailsAsync(context, 503, "Service Unavailable", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception");

            var detail = _environment.IsDevelopment()
                ? ex.ToString()
                : "An unexpected error occurred. Check server logs for details.";

            await WriteProblemDetailsAsync(context, 500, "Internal Server Error", detail);
        }
    }

    private static async Task WriteProblemDetailsAsync(
        HttpContext context,
        int statusCode,
        string title,
        string detail)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(problem, JsonOptions));
    }
}
