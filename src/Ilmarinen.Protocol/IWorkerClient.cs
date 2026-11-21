using Ilmarinen.Protocol.Responses;
using System.Threading.Tasks;

namespace Ilmarinen.Protocol;

/// <summary>What the server calls on a worker's connection: the server→worker half of the worker protocol.</summary>
public interface IWorkerClient
{
    Task AssignJob(JobAssignment assignment);
    Task CancelJob(string jobId);
    Task DeleteWorkspace(string workspaceName);
    /// <summary>What an operator's button asks for: the worker shows a "running" placeholder while it works.</summary>
    Task RunDiagnostic();

    /// <summary>The same diagnostic, asked for by the server rather than a person, so the worker reports only the verdict — nobody is watching a button for it.</summary>
    Task RecheckHost();
}
