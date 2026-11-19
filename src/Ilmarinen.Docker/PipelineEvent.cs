using System;

namespace Ilmarinen.Docker;

public enum PipelineEventKind
{
    StepStarted,

    /// <summary>The step's action is over and its container is about to be removed: the last moment its counters can be read.</summary>
    StepContainerEnding,

    StepEnded,
    PullStarted,
    PullEnded
}

/// <summary>Something that happened during a run, for observers such as resource sampling. Fields a kind doesn't carry are null.</summary>
public sealed record PipelineEvent
{
    public required PipelineEventKind Kind { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public int? StepIndex { get; init; }
    public string? StepName { get; init; }
    public string? Image { get; init; }

    /// <summary>On StepStarted: whether the step's image is one an earlier step of this run returned, which makes the step depend on that one. Matched by reference, so a step naming the same tag itself counts too.</summary>
    public bool? ImageFromEarlierStep { get; init; }

    /// <summary>On StepContainerEnding.</summary>
    public string? ContainerId { get; init; }

    /// <summary>On StepEnded: Success, Failed or Cancelled.</summary>
    public string? Status { get; init; }
}

/// <summary>Where a run is, as one consistent snapshot: the network its containers share, and the current step and its container, if one is running.</summary>
public sealed record PipelineProgress(string NetworkName, int? StepIndex, string? StepName, string? StepContainerId);
