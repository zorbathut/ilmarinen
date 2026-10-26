using Ilmarinen.Execution;
using Ilmarinen.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Docker;

/// <summary>
/// HTTP API server that exposes IJobContext methods to the injected agent CLI.
/// </summary>
public class AgentApiServer : IAsyncDisposable
{
    private HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _token;
    private Task? _listenerTask;
    private DockerJobContext? _currentContext;
    private int _disposed;

    public int Port { get; private set; }
    public string Token => _token;

    public AgentApiServer(int port = 0)
    {
        _token = Guid.NewGuid().ToString("N");
        _listener = null!; // Will be set in retry loop

        // Retry loop to handle port race condition
        const int maxAttempts = 10;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            int targetPort = port == 0 ? FindAvailablePort() : port;
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{targetPort}/");

            try
            {
                _listener.Start();
                Port = targetPort;
                _listenerTask = Task.Run(ListenAsync);
                return; // Success
            }
            catch (HttpListenerException) when (port == 0 && attempt < maxAttempts - 1)
            {
                // Port was grabbed by another process, try again (only if auto-allocating)
                _listener.Close();
            }
        }

        throw new InvalidOperationException(
            $"Failed to bind to an available port after {maxAttempts} attempts");
    }

    private static int FindAvailablePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void SetContext(DockerJobContext context)
    {
        _currentContext = context;
    }

    private async Task ListenAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context));
            }
            catch (HttpListenerException) when (_cts.Token.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            // Validate token
            var authHeader = request.Headers["Authorization"];
            if (authHeader != $"Bearer {_token}")
            {
                response.StatusCode = 401;
                await WriteJsonResponse(response, new { error = "Unauthorized" });
                return;
            }

            var path = request.Url?.AbsolutePath ?? "";
            var method = request.HttpMethod;

            // Ping is available without a job context — used by the diagnostic to verify a container can reach the agent API server (the most common DinD networking failure mode).
            if (method == "GET" && path == "/api/ping")
            {
                await WriteJsonResponse(response, new { status = "ok" });
                return;
            }

            if (_currentContext == null)
            {
                response.StatusCode = 503;
                await WriteJsonResponse(response, new { error = "No active context" });
                return;
            }

            // Route requests
            if (method == "POST" && path == "/api/run-command")
                await HandleRunCommand(request, response);
            else if (method == "POST" && path == "/api/build")
                await HandleBuild(request, response);
            else if (method == "GET" && path.StartsWith("/api/secret/"))
                await HandleSecret(request, response, path["/api/secret/".Length..]);
            else if (method == "GET" && path.StartsWith("/api/info/"))
                await HandleInfo(request, response, path["/api/info/".Length..]);
            else
            {
                response.StatusCode = 404;
                await WriteJsonResponse(response, new { error = "Not found" });
            }
        }
        catch (CommandException ex)
        {
            response.StatusCode = 422; // Unprocessable Entity - command ran but failed
            await WriteJsonResponse(response, new ErrorResponse(
                ex.Message,
                ex.GetType().Name,
                ex.ExitCode,
                ex.Stdout,
                ex.Stderr,
                null,
                null));
        }
        catch (Exception ex)
        {
            response.StatusCode = 500;
            await WriteJsonResponse(response, new ErrorResponse(
                ex.Message,
                ex.GetType().Name,
                null,
                null,
                null,
                null,
                null));
        }
        finally
        {
            response.Close();
        }
    }

    /// <summary>
    /// The docker command line for a nested container, for the agent to run in the step itself. Composing it needs what only this side knows — the workspace's host path, the job's network, the user and docker group — but running it from the step means the nested container's output is simply the step's own output, live and byte for byte.
    /// </summary>
    private async Task HandleRunCommand(HttpListenerRequest request, HttpListenerResponse response)
    {
        var body = await ReadJsonBody<RunRequest>(request);
        await WriteTextResponse(response, _currentContext!.BuildDockerRunCommand(ImageRef.From(body.Image), body.Command ?? []));
    }

    private async Task HandleBuild(HttpListenerRequest request, HttpListenerResponse response)
    {
        var body = await ReadJsonBody<BuildRequest>(request);
        var image = await _currentContext!.BuildImage(body.Dockerfile, body.Tag, body.Context, body.BuildArgs);

        await WriteJsonResponse(response, new { reference = image.Reference });
    }

    private async Task HandleSecret(HttpListenerRequest request, HttpListenerResponse response, string name)
    {
        try
        {
            var value = _currentContext!.Secret(name);
            await WriteJsonResponse(response, new { value });
        }
        catch (InvalidOperationException ex)
        {
            response.StatusCode = 404;
            await WriteJsonResponse(response, new { error = ex.Message });
        }
    }

    private async Task HandleInfo(HttpListenerRequest request, HttpListenerResponse response, string key)
    {
        var value = key.ToLowerInvariant() switch
        {
            "branch" => _currentContext!.Branch,
            "commit" => _currentContext!.Commit,
            _ => null
        };

        if (value == null)
        {
            response.StatusCode = 404;
            await WriteJsonResponse(response, new { error = $"Unknown info key: {key}" });
            return;
        }

        await WriteJsonResponse(response, new { value });
    }

    private static async Task<T> ReadJsonBody<T>(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var json = await reader.ReadToEndAsync();
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to parse request body");
    }

    private static async Task WriteTextResponse(HttpListenerResponse response, string text)
    {
        var buffer = Encoding.UTF8.GetBytes(text);
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer);
    }

    private static async Task WriteJsonResponse(HttpListenerResponse response, object data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        var buffer = Encoding.UTF8.GetBytes(json);
        response.ContentType = "application/json";
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cts.Cancel();

        // Close() alone tears the listener down — do NOT also call Stop(). On Unix both go through
        // HttpEndPointManager.RemoveListener: the first drops the (address, port) entry and frees the socket,
        // and the second, finding no entry, *re-binds the port* just to unregister the prefix. Any port picked
        // up in between — routinely, by a sibling server's FindAvailablePort — makes that re-bind throw
        // "Address already in use" out of teardown.
        _listener.Close();

        if (_listenerTask != null)
        {
            // Close() makes the pending GetContextAsync throw ObjectDisposedException, which ListenAsync treats as its stop signal.
            await _listenerTask;
        }

        _cts.Dispose();
    }

    // Request/Response DTOs
    private record RunRequest(string Image, string[]? Command);
    private record BuildRequest(string Dockerfile, string? Tag, string? Context, Dictionary<string, string>? BuildArgs);

    /// <summary>
    /// Structured error response for API errors.
    /// </summary>
    private record ErrorResponse(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("exceptionType")] string? ExceptionType,
        [property: JsonPropertyName("exitCode")] int? ExitCode,
        [property: JsonPropertyName("stdout")] string? Stdout,
        [property: JsonPropertyName("stderr")] string? Stderr,
        [property: JsonPropertyName("nestingDepth")] int? NestingDepth,
        [property: JsonPropertyName("containerChain")] string[]? ContainerChain);
}
