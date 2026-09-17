using System.ComponentModel.DataAnnotations;

namespace Ilmarinen.Protocol.Requests;

public record WorkerUpdate
{
    // System.Text.Json deserializes any number into an enum; declaring the range here makes [ApiController] reject the rest with a 400.
    [EnumDataType(typeof(WorkerPriority))]
    public required WorkerPriority Priority { get; init; }
}
