using Ilmarinen.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using System.IO;
using System.Threading.Tasks;
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

    private class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Ilmarinen.Server";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
