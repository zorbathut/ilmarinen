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
    /// <summary>A worker priority's name; null or "any" for no minimum.</summary>
    public string? MinWorkerPriority { get; init; }
    /// <summary>A job priority's name; null for the default one.</summary>
    public string? Priority { get; init; }
    [EnumDataType(typeof(NotificationPolicy))]
    public NotificationPolicy NotificationPolicy { get; init; }
}
