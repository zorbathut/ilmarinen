using Ilmarinen.Execution;
using Ilmarinen.Models;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Docker;

/// <summary>
/// HTTP API server that exposes IJobContext methods to the injected agent CLI.
///
/// Requests are JSON, but every response is plain text: a value on success, a message with a non-2xx status on failure. The agent is a POSIX shell script with no JSON parser to lean on, and decoding JSON strings by hand in sed mangles them.
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
                await WriteErrorResponse(response, 401, "Unauthorized");
                return;
            }

            var path = request.Url?.AbsolutePath ?? "";
            var method = request.HttpMethod;

            // Ping is available without a job context — used by the diagnostic to verify a container can reach the agent API server (the most common DinD networking failure mode).
            if (method == "GET" && path == "/api/ping")
            {
                await WriteTextResponse(response, "ok");
                return;
            }

            if (_currentContext == null)
            {
                await WriteErrorResponse(response, 503, "No active context");
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
                await WriteErrorResponse(response, 404, "Not found");
            }
        }
        catch (CommandException ex)
        {
            // Unprocessable Entity - command ran but failed
            await WriteErrorResponse(response, 422, ex.Message);
        }
        catch (Exception ex)
        {
            await WriteErrorResponse(response, 500, $"{ex.GetType().Name}: {ex.Message}");
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

        await WriteTextResponse(response, image.Reference);
    }

    private async Task HandleSecret(HttpListenerRequest request, HttpListenerResponse response, string name)
    {
        try
        {
            var value = _currentContext!.Secret(name);
            await WriteTextResponse(response, value);
        }
        catch (InvalidOperationException ex)
        {
            await WriteErrorResponse(response, 404, ex.Message);
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
            await WriteErrorResponse(response, 404, $"Unknown info key: {key}");
            return;
        }

        await WriteTextResponse(response, value);
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

    /// <summary>
    /// The message is the body, for curl; a one-line form of it is also the status line's reason phrase, since wget keeps no body for an error response but does report the status line.
    /// </summary>
    private static async Task WriteErrorResponse(HttpListenerResponse response, int statusCode, string message)
    {
        response.StatusCode = statusCode;

        // A reason phrase is one line of printable ASCII.
        var reason = new StringBuilder();
        foreach (var c in message)
        {
            reason.Append(c is >= ' ' and <= '~' ? c : c is '\n' or '\r' or '\t' ? ' ' : '?');
            if (reason.Length >= MaxReasonPhraseLength)
            {
                break;
            }
        }

        response.StatusDescription = reason.ToString();
        await WriteTextResponse(response, message);
    }

    private const int MaxReasonPhraseLength = 200;

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

    // Request DTOs
    private record RunRequest(string Image, string[]? Command);
    private record BuildRequest(string Dockerfile, string? Tag, string? Context, Dictionary<string, string>? BuildArgs);
}
