using Ilmarinen.Protocol.Requests;
using Ilmarinen.Protocol.Responses;
using Ilmarinen.Protocol;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// The worker's side of the protocol as data: what it is doing, the outcomes the server hasn't acknowledged, and whether a diagnostic is waiting to run. Every change bumps <see cref="WorkerState.Seq"/>, and the caller reports the new state after it. Not thread-safe: WorkerService holds its state lock around every call, including across a Resume, so nothing changes between the snapshot Resume sends and the server's answer.
/// </summary>
internal sealed class WorkerProtocolState
{
    private readonly List<JobOutcome> _outcomes = [];
    private long _seq;

    public WorkerActivity Activity { get; private set; } = WorkerActivity.Idle;

    /// <summary>The assignment being run, while <see cref="Activity"/> is RunningJob.</summary>
    public JobAssignment? Job { get; private set; }

    public DiagnosticStatus? DiagnosticStatus { get; private set; }

    /// <summary>A diagnostic was asked for while the worker was busy; it runs as soon as the worker is idle.</summary>
    public bool DiagnosticWanted { get; private set; }

    public IReadOnlyList<JobOutcome> PendingOutcomes
    {
        get { return _outcomes.ToList(); }
    }

    public WorkerState Snapshot()
    {
        return new WorkerState
        {
            Seq = _seq,
            Activity = Activity,
            JobId = Job?.Id,
            AssignmentId = Job?.AssignmentId,
            DiagnosticStatus = DiagnosticStatus
        };
    }

    /// <summary>Takes the job if idle and returns null; otherwise returns the hand-back to report, and the server queues the job again.</summary>
    public JobOutcome? Assign(JobAssignment assignment)
    {
        if (Activity != WorkerActivity.Idle)
        {
            return HandBack(assignment);
        }

        Activity = WorkerActivity.RunningJob;
        Job = assignment;
        _seq++;
        return null;
    }

    /// <summary>Gives a job back unrun, whatever the worker is doing (one that is shutting down takes nothing new).</summary>
    public JobOutcome HandBack(JobAssignment assignment)
    {
        var handBack = new JobOutcome
        {
            JobId = assignment.Id,
            AssignmentId = assignment.AssignmentId,
            Kind = JobOutcomeKind.Rejected
        };
        _outcomes.Add(handBack);
        return handBack;
    }

    /// <summary>Records how the running job went and goes idle, as one step: there is never a moment when the worker is idle with the job's outcome unrecorded.</summary>
    public JobOutcome FinishJob(JobStatus status, TimeSpan duration, IReadOnlyList<WorkspaceInfo> workspaces)
    {
        if (Activity != WorkerActivity.RunningJob || Job == null)
        {
            throw new InvalidOperationException($"No job is running (activity: {Activity})");
        }

        var outcome = new JobOutcome
        {
            JobId = Job.Id,
            AssignmentId = Job.AssignmentId,
            Kind = JobOutcomeKind.Completed,
            Status = status,
            Duration = duration,
            Workspaces = workspaces
        };
        _outcomes.Add(outcome);
        Activity = WorkerActivity.Idle;
        Job = null;
        _seq++;
        return outcome;
    }

    public void Acknowledge(IEnumerable<JobOutcome> outcomes)
    {
        foreach (var outcome in outcomes)
        {
            _outcomes.RemoveAll(o => o.AssignmentId == outcome.AssignmentId && o.Kind == outcome.Kind);
        }
    }

    /// <summary>The server asking for a diagnostic. Starts one if idle (returns true); otherwise one runs once the worker is idle — never folded into a diagnostic already running, which may have started before the request was made.</summary>
    public bool RequestDiagnostic()
    {
        if (Activity == WorkerActivity.Idle)
        {
            StartDiagnostic();
            return true;
        }

        DiagnosticWanted = true;
        return false;
    }

    /// <summary>Starts a diagnostic if idle and one is wanted, or if <paramref name="unprompted"/> (the worker's own: its first, or a retry of an unhealthy one).</summary>
    public bool TryStartDiagnostic(bool unprompted)
    {
        if (Activity != WorkerActivity.Idle || !(DiagnosticWanted || unprompted))
        {
            return false;
        }

        StartDiagnostic();
        return true;
    }

    /// <summary>Ends the running diagnostic with its verdict, or with none if it was cut short (by shutdown), keeping the previous one.</summary>
    public void FinishDiagnostic(DiagnosticStatus? verdict)
    {
        if (Activity != WorkerActivity.RunningDiagnostic)
        {
            throw new InvalidOperationException($"No diagnostic is running (activity: {Activity})");
        }

        Activity = WorkerActivity.Idle;
        if (verdict != null)
        {
            DiagnosticStatus = verdict;
        }
        _seq++;
    }

    private void StartDiagnostic()
    {
        Activity = WorkerActivity.RunningDiagnostic;
        DiagnosticWanted = false;
        _seq++;
    }
}
