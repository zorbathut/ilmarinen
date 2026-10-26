using Ilmarinen.Protocol;
using System.Diagnostics;
using System.Net.Http;
using System.Net;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Ilmarinen.Cli;

public sealed class JobNotFoundException(Guid jobId) : Exception($"Job {jobId} not found");

/// <summary>
/// Follows a job's log to its end by polling the server's chunks feed forward from the last sequence seen. The feed is the only reader of stored log, and an empty read shakes loose anything still in the server's persistence buffer, so polling it is enough to see everything: the job is over once a read comes back empty with a terminal status, because the server persists a job's log before it marks the job finished.
///
/// Two kinds of output fall outside that. A chunk that reaches the server late with a sequence below one already read — a worker replaying what it couldn't send while disconnected — is never emitted, since the feed only reads forward. And a cancelled job is terminal the moment the cancel lands, before its worker has stopped, so whatever the worker writes while tearing down comes after the follow has ended; the worker can even finish the job anyway, overriding Cancelled with its real outcome. The stored log has all of it.
/// </summary>
public sealed class JobLogFollower
{
    private readonly HttpClient _http;
    private readonly Guid _jobId;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _giveUpAfter;
    private readonly Action<LogEntry> _onEntry;
    private readonly Action<JobStatus> _onStatusChanged;
    private readonly Action<string> _onWarning;

    /// <param name="giveUpAfter">How long the server may stay unreachable before the follow throws.</param>
    public JobLogFollower(HttpClient http, Guid jobId, TimeSpan pollInterval, TimeSpan giveUpAfter, Action<LogEntry> onEntry, Action<JobStatus> onStatusChanged, Action<string> onWarning)
    {
        _http = http;
        _jobId = jobId;
        _pollInterval = pollInterval;
        _giveUpAfter = giveUpAfter;
        _onEntry = onEntry;
        _onStatusChanged = onStatusChanged;
        _onWarning = onWarning;
    }

    public async Task<JobStatus> FollowAsync(CancellationToken cancellationToken)
    {
        var last = 0;
        JobStatus? status = null;
        Stopwatch? failing = null;

        while (true)
        {
            var (page, failure) = await TryReadPageAsync(last, status != null, cancellationToken);

            if (page == null)
            {
                // Once per outage rather than once per retry: a redeploy would otherwise bury the log under warnings.
                if (failing == null)
                {
                    _onWarning($"{failure}; retrying.");
                    failing = Stopwatch.StartNew();
                }
                else if (failing.Elapsed > _giveUpAfter)
                {
                    throw new HttpRequestException($"Gave up following job {_jobId}: the server has been unreachable for {failing.Elapsed.TotalSeconds:0}s. Last failure: {failure}");
                }

                await Task.Delay(_pollInterval, cancellationToken);
                continue;
            }

            if (failing != null)
            {
                _onWarning("Reconnected to the server.");
                failing = null;
            }

            if (page.Status != status)
            {
                status = page.Status;
                _onStatusChanged(page.Status);
            }

            if (page.LastSequence != null)
            {
                foreach (var entry in LogChunkParser.ParseEntries(page.Body))
                {
                    _onEntry(entry);
                }

                last = page.LastSequence.Value;
                continue;
            }

            if (page.Status is JobStatus.Success or JobStatus.Failed or JobStatus.Cancelled)
            {
                return page.Status;
            }

            await Task.Delay(_pollInterval, cancellationToken);
        }
    }

    private sealed record Page(JobStatus Status, int? LastSequence, string Body);

    /// <summary>
    /// One forward read of the feed, or why the server couldn't give one — the failures worth retrying through, such as a server that is restarting.
    /// </summary>
    /// <param name="jobSeen">Whether an earlier read found the job. A 404 after that is the route to the server going away during a redeploy, not the job.</param>
    private async Task<(Page? Page, string? Failure)> TryReadPageAsync(int after, bool jobSeen, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;

        try
        {
            response = await _http.GetAsync($"api/jobs/{_jobId}/logs/chunks?after={after}", cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Could not reach the server ({ex.Message})");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, $"The server did not answer in time ({ex.Message})");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound && !jobSeen)
            {
                throw new JobNotFoundException(_jobId);
            }

            if (response.StatusCode == HttpStatusCode.NotFound || (int)response.StatusCode >= 500)
            {
                return (null, $"The server returned {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            response.EnsureSuccessStatusCode();

            var status = Enum.Parse<JobStatus>(Header(response, "X-Job-Status")!);
            var lastSequence = Header(response, "X-Log-Last-Sequence");
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            return (new Page(status, lastSequence != null ? int.Parse(lastSequence) : null, body), null);
        }
    }

    private static string? Header(HttpResponseMessage response, string name)
    {
        return response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
    }
}
