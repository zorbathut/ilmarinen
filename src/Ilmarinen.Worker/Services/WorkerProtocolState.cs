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
    private readonly HashSet<WorkerHold> _holds = [];
    private long _seq;

    public WorkerActivity Activity { get; private set; } = WorkerActivity.Idle;

    /// <summary>The assignment being run, while <see cref="Activity"/> is RunningJob.</summary>
    public JobAssignment? Job { get; private set; }

    public DiagnosticStatus? DiagnosticStatus { get; private set; }

    /// <summary>The persistent workspace the running job is using, once it has named one.</summary>
    public string? Workspace { get; private set; }

    /// <summary>A diagnostic was asked for while the worker was busy; it runs as soon as the worker is idle.</summary>
    public bool DiagnosticWanted { get; private set; }

    /// <summary>The hold the worker reports: of those in force, the latest-declared, so the reason given is the one that will last longest.</summary>
    public WorkerHold? Hold
    {
        get { return _holds.Count == 0 ? null : _holds.Max(); }
    }

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
            DiagnosticStatus = DiagnosticStatus,
            Hold = Hold,
            Workspace = Workspace
        };
    }

    /// <summary>Takes the job if idle and not held and returns null; otherwise returns the hand-back to report, and the server queues the job again.</summary>
    public JobOutcome? Assign(JobAssignment assignment)
    {
        if (Activity != WorkerActivity.Idle || Hold != null)
        {
            return HandBack(assignment);
        }

        Activity = WorkerActivity.RunningJob;
        Job = assignment;
        _seq++;
        return null;
    }

    /// <summary>Puts a hold on or takes it off; returns whether the reported hold changed, and so whether there is a new state to report.</summary>
    public bool SetHold(WorkerHold hold, bool held)
    {
        var before = Hold;
        if (held)
        {
            _holds.Add(hold);
        }
        else
        {
            _holds.Remove(hold);
        }

        if (Hold == before)
        {
            return false;
        }
        _seq++;
        return true;
    }

    private JobOutcome HandBack(JobAssignment assignment)
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
    public JobOutcome FinishJob(JobStatus status, TimeSpan duration, IReadOnlyList<WorkspaceInfo> workspaces, JobDiskUse diskUse)
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
            Workspaces = workspaces,
            DiskUse = diskUse
        };
        _outcomes.Add(outcome);
        Activity = WorkerActivity.Idle;
        Job = null;
        Workspace = null;
        _seq++;
        return outcome;
    }

    /// <summary>Records the workspace the running job is using; returns whether that changed, and so whether there is a new state to report. <see cref="FinishJob"/> clears it.</summary>
    public bool SetWorkspace(string name)
    {
        if (Activity != WorkerActivity.RunningJob)
        {
            throw new InvalidOperationException($"No job is running to use workspace {name} (activity: {Activity})");
        }
        if (Workspace == name)
        {
            return false;
        }
        Workspace = name;
        _seq++;
        return true;
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
