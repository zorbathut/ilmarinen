using System.ComponentModel.DataAnnotations;
using System;

namespace Ilmarinen.Protocol.Requests;

public record PipelineUpdate
{
    public string? Name { get; init; }
    public Guid? RepositoryId { get; init; }
    public string? Ref { get; init; }
    public string? ScriptPath { get; init; }

    public string? Schedule { get; init; }
    public bool ClearSchedule { get; init; }

    /// <summary>A worker priority's name, or "any" to clear the minimum; null leaves it as it is.</summary>
    public string? MinWorkerPriority { get; init; }

    /// <summary>A job priority's name; null leaves it as it is.</summary>
    public string? Priority { get; init; }

    [EnumDataType(typeof(NotificationPolicy))]
    public NotificationPolicy? NotificationPolicy { get; init; }
}
