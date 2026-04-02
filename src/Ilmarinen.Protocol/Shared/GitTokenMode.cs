namespace Ilmarinen.Protocol;

/// <summary>
/// How a job's git credentials are resolved.
/// </summary>
public enum GitTokenMode
{
    /// <summary>No git token — public repo access only.</summary>
    None,

    /// <summary>Inherit the token from the pipeline's repository.</summary>
    Inherit,

    /// <summary>Use an explicitly provided token.</summary>
    Explicit
}
