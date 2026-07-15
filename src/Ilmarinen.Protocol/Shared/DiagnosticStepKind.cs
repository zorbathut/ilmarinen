namespace Ilmarinen.Protocol;

/// <summary>
/// How a step's outcome should be read. Success says what happened; this says what it means.
/// </summary>
public enum DiagnosticStepKind
{
    /// <summary>Failing means the worker cannot run jobs.</summary>
    Capability,

    /// <summary>Failing means the diagnostic left something behind, but the worker still works.</summary>
    Hygiene,

    /// <summary>Reports an optional capability the worker does not require. Never affects health.</summary>
    Advisory
}
