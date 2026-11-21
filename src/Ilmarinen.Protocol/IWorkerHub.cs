using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Protocol;

/// <summary>
/// What a worker calls on the server's hub: the worker→server half of the worker protocol (spec/README.md), with <see cref="IWorkerClient"/> the other half. The hub implements these as its public methods, which is how SignalR finds them, so an explicit implementation would compile and never be called.
/// </summary>
public interface IWorkerHub
{
    Task<AuthChallenge> Connect(WorkerConnect request);
    Task<SessionAnswer> Authenticate(WorkerAuthenticate info);
    Task<ResumeResponse> Resume(WorkerResume resume);
    Task<SessionAnswer> ReportState(WorkerState state);
    Task<SessionAnswer> ReportOutcome(JobOutcome outcome);
    Task<SessionAnswer> Heartbeat();
    Task<SessionAnswer> ReportCommit(Guid jobId, string commitSha);
    Task<SessionAnswer> StreamMetrics(JobMetricsChunk chunk);
    Task<SessionAnswer> StreamLogs(LogChunk chunk);
    Task<SessionAnswer> WorkspaceDeleted(string name, DeleteWorkspaceResult result);
    SessionAnswer ReportDiagnostic(DiagnosticReport report);
    Task<SessionAnswer> ReportDiskSpace(DiskSpaceReport report);
    Task<SessionAnswer> ReportWorkspaceUsage(WorkspaceUsageReport report);
}
