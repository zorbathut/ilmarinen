using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Threading.Tasks;
using System;

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
        catch (InvalidSubmissionException ex)
        {
            _logger.LogInformation("Rejected submission: {Message}", ex.Message);
            await WriteProblemDetailsAsync(context, 400, "Bad Request", ex.Message);
        }
        catch (ConfigurationException ex)
        {
            _logger.LogWarning(ex, "Configuration error: {Message}", ex.Message);
            await WriteProblemDetailsAsync(context, 503, "Service Unavailable", ex.Message);
        }
        // Keyed on the client going away rather than on an exception type: a hang-up mid-response surfaces as a cancellation, a reset connection or a disposed pipe depending on where the write was, and none of them are a server fault.
        catch (Exception ex) when (context.RequestAborted.IsCancellationRequested)
        {
            _logger.LogInformation("Client aborted {Method} {Path}: {Message}", context.Request.Method, context.Request.Path, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception");

            if (context.Response.HasStarted)
            {
                // Part of the response is already on the wire, so a ProblemDetails body would only corrupt it. Resetting the connection is the only way left to tell the client the payload is incomplete.
                context.Abort();
                return;
            }

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
