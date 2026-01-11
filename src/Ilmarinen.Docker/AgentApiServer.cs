using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ilmarinen.Execution;
using Ilmarinen.Models;

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

            if (_currentContext == null)
            {
                response.StatusCode = 503;
                await WriteJsonResponse(response, new { error = "No active context" });
                return;
            }

            var path = request.Url?.AbsolutePath ?? "";
            var method = request.HttpMethod;

            // Route requests
            if (method == "POST" && path == "/api/run")
                await HandleRun(request, response);
            else if (method == "POST" && path == "/api/build")
                await HandleBuild(request, response);
            else if (method == "POST" && path == "/api/service/start")
                await HandleServiceStart(request, response);
            else if (method == "POST" && path == "/api/service/stop")
                await HandleServiceStop(request, response);
            else if (method == "POST" && path == "/api/service/wait")
                await HandleServiceWait(request, response);
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
        catch (NestedContainerException ex)
        {
            response.StatusCode = 422; // Unprocessable Entity - command ran but failed
            await WriteJsonResponse(response, new ErrorResponse(
                ex.Message,
                ex.GetType().Name,
                ex.ExitCode,
                ex.Stdout,
                ex.Stderr,
                ex.NestingDepth,
                ex.ContainerChain.ToArray()));
        }
        catch (CommandException ex)
        {
            response.StatusCode = 422;
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

    private async Task HandleRun(HttpListenerRequest request, HttpListenerResponse response)
    {
        var body = await ReadJsonBody<RunRequest>(request);

        // Stream NDJSON response in real-time
        response.ContentType = "application/x-ndjson";
        response.SendChunked = true;

        try
        {
            await _currentContext!.RunStreaming(
                response.OutputStream,
                ImageRef.From(body.Image),
                body.Command ?? []);
        }
        catch (NestedContainerException ex)
        {
            // Write error as final NDJSON line
            var newChain = new List<string>(ex.ContainerChain) { body.Image };
            var errorJson = JsonSerializer.Serialize(new
            {
                t = "x",
                c = ex.ExitCode,
                error = ex.Message,
                exceptionType = ex.GetType().Name,
                nestingDepth = ex.NestingDepth + 1,
                containerChain = newChain
            }, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(errorJson + "\n");
            await response.OutputStream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            // Write generic error as final NDJSON line
            var errorJson = JsonSerializer.Serialize(new
            {
                t = "x",
                c = -1,
                error = ex.Message,
                exceptionType = ex.GetType().Name
            }, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(errorJson + "\n");
            await response.OutputStream.WriteAsync(bytes);
        }
    }

    private async Task HandleBuild(HttpListenerRequest request, HttpListenerResponse response)
    {
        var body = await ReadJsonBody<BuildRequest>(request);
        var image = await _currentContext!.BuildImage(body.Dockerfile, body.Tag, body.Context);

        await WriteJsonResponse(response, new { reference = image.Reference });
    }

    private async Task HandleServiceStart(HttpListenerRequest request, HttpListenerResponse response)
    {
        var body = await ReadJsonBody<ServiceStartRequest>(request);
        var handle = await _currentContext!.StartService(
            ImageRef.From(body.Image),
            body.Name,
            body.Ports);

        await WriteJsonResponse(response, new { name = handle.Name });
    }

    private async Task HandleServiceStop(HttpListenerRequest request, HttpListenerResponse response)
    {
        var body = await ReadJsonBody<ServiceStopRequest>(request);
        // Note: We need to track services by name to stop them
        // For now, use shell command directly
        await _currentContext!.Shell($"docker stop {body.Name} && docker rm {body.Name}");

        await WriteJsonResponse(response, new { success = true });
    }

    private async Task HandleServiceWait(HttpListenerRequest request, HttpListenerResponse response)
    {
        var body = await ReadJsonBody<ServiceWaitRequest>(request);
        var timeout = body.TimeoutSeconds.HasValue
            ? TimeSpan.FromSeconds(body.TimeoutSeconds.Value)
            : (TimeSpan?)null;

        try
        {
            await _currentContext!.WaitForHealthy(body.Url, timeout);
            await WriteJsonResponse(response, new { healthy = true });
        }
        catch (TimeoutException)
        {
            await WriteJsonResponse(response, new { healthy = false });
        }
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
        _cts.Cancel();
        _listener.Stop();
        if (_listenerTask != null)
        {
            await _listenerTask;
        }
        _listener.Close();
        _cts.Dispose();
    }

    // Request/Response DTOs
    private record RunRequest(string Image, string[]? Command);
    private record BuildRequest(string Dockerfile, string? Tag, string? Context);
    private record ServiceStartRequest(string Image, string Name, int[]? Ports);
    private record ServiceStopRequest(string Name);
    private record ServiceWaitRequest(string Url, int? TimeoutSeconds);

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
