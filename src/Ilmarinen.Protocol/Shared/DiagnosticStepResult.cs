using System;

namespace Ilmarinen.Protocol;

public record DiagnosticStepResult
{
    public required string Name { get; init; }
    public required bool Success { get; init; }
    public string? Message { get; init; }
    public FailureKind Failure { get; init; }
    public string? Suggestion { get; init; }
    public TimeSpan Duration { get; init; }
}
