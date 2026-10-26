using Ilmarinen.Cli;
using Ilmarinen.Protocol;
using NUnit.Framework;
using System.Collections.Generic;
using System.Net.Http;
using System.Net;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

/// <summary>
/// The follower's behavior while the server is unreachable, which a real server can't be made to act out on demand — so the HTTP boundary is replaced by a scripted handler that answers the feed one response at a time.
/// </summary>
[TestFixture]
public class JobLogFollowerRetryTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    [Test]
    public async Task Follow_ServerUnreachableThenBack_WarnsOnceEachWayAndCarriesOn()
    {
        var handler = new ScriptedHandler(
            Unreachable,
            Unreachable,
            Unreachable,
            () => Page(JobStatus.Success));
        var warnings = new List<string>();

        var status = await FollowAsync(handler, TimeSpan.FromMinutes(1), warnings);

        Assert.That(status, Is.EqualTo(JobStatus.Success));
        Assert.That(warnings, Has.Count.EqualTo(2), "one warning when the outage starts and one when it ends, not one per retry");
        Assert.That(warnings[0], Is.Not.EqualTo(warnings[1]));
    }

    [Test]
    public void Follow_ServerUnreachableForGood_GivesUp()
    {
        var handler = new ScriptedHandler(Unreachable);

        Assert.ThrowsAsync<HttpRequestException>(async () => await FollowAsync(handler, TimeSpan.FromMilliseconds(100), []));
    }

    /// <summary>A proxy in front of a restarting server answers 404 for everything, which is not the job having gone.</summary>
    [Test]
    public async Task Follow_NotFoundAfterTheJobWasSeen_IsRetried()
    {
        var handler = new ScriptedHandler(
            () => Page(JobStatus.Running),
            () => new HttpResponseMessage(HttpStatusCode.NotFound),
            () => Page(JobStatus.Success));
        var warnings = new List<string>();

        var status = await FollowAsync(handler, TimeSpan.FromMinutes(1), warnings);

        Assert.That(status, Is.EqualTo(JobStatus.Success));
        Assert.That(warnings, Has.Count.EqualTo(2));
    }

    [Test]
    public void Follow_ServerErrorThatIsNotTransient_Throws()
    {
        var handler = new ScriptedHandler(() => new HttpResponseMessage(HttpStatusCode.BadRequest));

        Assert.ThrowsAsync<HttpRequestException>(async () => await FollowAsync(handler, TimeSpan.FromMinutes(1), []));
    }

    private static async Task<JobStatus> FollowAsync(HttpMessageHandler handler, TimeSpan giveUpAfter, List<string> warnings)
    {
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://ilmarinen.invalid/") };
        var follower = new JobLogFollower(http, Guid.NewGuid(), PollInterval, giveUpAfter, _ => { }, _ => { }, warnings.Add);
        return await follower.FollowAsync(CancellationToken.None);
    }

    private static HttpResponseMessage Unreachable()
    {
        throw new HttpRequestException("Connection refused");
    }

    /// <summary>An empty page of the feed: no chunks, just the status.</summary>
    private static HttpResponseMessage Page(JobStatus status)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
        response.Headers.Add("X-Job-Status", status.ToString());
        return response;
    }

    /// <summary>Answers each request with the next scripted response, repeating the last one once the script runs out.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage>[] _script;
        private int _next;

        public ScriptedHandler(params Func<HttpResponseMessage>[] script)
        {
            _script = script;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var step = _script[Math.Min(_next, _script.Length - 1)];
            _next++;
            return Task.FromResult(step());
        }
    }
}
