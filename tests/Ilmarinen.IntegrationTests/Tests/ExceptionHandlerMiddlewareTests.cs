using Ilmarinen.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// Pins the error-handling policy: an operator's misconfiguration or a bad submission must never be
/// reported as a 500, which is reserved for genuine bugs and infrastructure failures.
/// </summary>
[TestFixture]
public class ExceptionHandlerMiddlewareTests
{
    private static async Task<(int status, string body)> InvokeWithAsync(Exception thrown, string environmentName)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        var middleware = new ExceptionHandlerMiddleware(
            _ => throw thrown,
            new StubHostEnvironment { EnvironmentName = environmentName },
            NullLogger<ExceptionHandlerMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, body);
    }

    [Test]
    public async Task ConfigurationException_Returns503_NotServerError()
    {
        var (status, body) = await InvokeWithAsync(
            new ConfigurationException("ILMARINEN_SERVER_KEY must be valid base64."),
            Environments.Production);

        Assert.That(status, Is.EqualTo(503));
        Assert.That(body, Does.Contain("ILMARINEN_SERVER_KEY must be valid base64."));
    }

    [Test]
    public async Task InvalidSubmissionException_Returns400()
    {
        var (status, body) = await InvokeWithAsync(
            new InvalidSubmissionException("Unknown pipeline nope."),
            Environments.Production);

        Assert.That(status, Is.EqualTo(400));
        Assert.That(body, Does.Contain("Unknown pipeline nope."));
    }

    [Test]
    public async Task UnexpectedException_Returns500_AndHidesDetailInProduction()
    {
        var (status, body) = await InvokeWithAsync(
            new InvalidOperationException("Connection string leaked in here"),
            Environments.Production);

        Assert.That(status, Is.EqualTo(500));
        Assert.That(body, Does.Not.Contain("Connection string leaked in here"));
        Assert.That(body, Does.Contain("Check server logs"));
    }

    [Test]
    public async Task UnexpectedException_IncludesDetailInDevelopment()
    {
        var (status, body) = await InvokeWithAsync(
            new InvalidOperationException("Something went bang"),
            Environments.Development);

        Assert.That(status, Is.EqualTo(500));
        Assert.That(body, Does.Contain("Something went bang"));
    }

    /// <summary>
    /// A failure part-way through a streamed response can't be turned into a ProblemDetails, so the
    /// connection must be reset: finishing normally would hand the client a clean 200 over a
    /// truncated payload.
    /// </summary>
    [Test]
    public async Task ResponseAlreadyStarted_ResetsConnectionAndLeavesThePartialResponseAlone()
    {
        var context = new DefaultHttpContext();
        var lifetime = new StubRequestLifetimeFeature();
        context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        context.Response.Body = new MemoryStream();

        var middleware = new ExceptionHandlerMiddleware(
            async ctx =>
            {
                await ctx.Response.WriteAsync("half a log file");
                throw new InvalidOperationException("failed mid-stream");
            },
            new StubHostEnvironment(),
            NullLogger<ExceptionHandlerMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();

        Assert.That(lifetime.Aborted, Is.True, "a truncated response must not be completed as a success");
        Assert.That(body, Is.EqualTo("half a log file"));
        Assert.That(context.Response.StatusCode, Is.EqualTo(200));
    }

    [Test]
    public async Task ClientAbortedRequest_IsNotReportedAsAServerError()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.RequestAborted = new CancellationToken(canceled: true);

        var middleware = new ExceptionHandlerMiddleware(
            _ => throw new OperationCanceledException(),
            new StubHostEnvironment(),
            NullLogger<ExceptionHandlerMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();

        Assert.That(context.Response.StatusCode, Is.EqualTo(200));
        Assert.That(body, Is.Empty);
    }

    /// <summary>
    /// The abort path keys on the client hanging up, not on the exception type — a cancellation
    /// raised while the client is still connected is a bug and must still be reported as one.
    /// </summary>
    [Test]
    public async Task CancellationWithoutClientAbort_StillReturns500()
    {
        var (status, body) = await InvokeWithAsync(
            new OperationCanceledException("a stray token fired"),
            Environments.Production);

        Assert.That(status, Is.EqualTo(500));
        Assert.That(body, Does.Contain("Check server logs"));
    }

    private class StartedResponseFeature : HttpResponseFeature
    {
        public override bool HasStarted
        {
            get { return true; }
        }
    }

    private class StubRequestLifetimeFeature : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }
        public CancellationToken RequestAborted { get; set; } = CancellationToken.None;

        public void Abort()
        {
            Aborted = true;
        }
    }

    private class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Ilmarinen.Server";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
