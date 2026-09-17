using System.ComponentModel.DataAnnotations;

namespace Ilmarinen.Protocol.Requests;

public record PipelineTrigger
{
    public string? Ref { get; init; }
    [EnumDataType(typeof(WorkerPriority))]
    public WorkerPriority? MinWorkerPriority { get; init; }
}
