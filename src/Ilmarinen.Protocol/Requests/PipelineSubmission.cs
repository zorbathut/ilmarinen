using System.ComponentModel.DataAnnotations;
using System;

namespace Ilmarinen.Protocol.Requests;

public record PipelineSubmission
{
    public required string Name { get; init; }
    public required Guid RepositoryId { get; init; }
    public required string Ref { get; init; }
    public required string ScriptPath { get; init; }
    public string? Schedule { get; init; }
    [EnumDataType(typeof(WorkerPriority))]
    public WorkerPriority MinWorkerPriority { get; init; }
    [EnumDataType(typeof(JobPriority))]
    public JobPriority Priority { get; init; }
    [EnumDataType(typeof(NotificationPolicy))]
    public NotificationPolicy NotificationPolicy { get; init; }
}
