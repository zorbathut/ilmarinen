namespace Ilmarinen.Protocol;

/// <summary>Where a punted job would go next, as dispatch stands when the punt is asked for.</summary>
public enum PuntOutcome
{
    /// <summary>Another worker takes it.</summary>
    GoesElsewhere = 0,

    /// <summary>Nothing else would take it, and the worker it is punted from would, as soon as it is idle: punting it achieves nothing.</summary>
    ComesBack = 1,

    /// <summary>Nothing takes it for now, the worker it is punted from included.</summary>
    Waits = 2
}
