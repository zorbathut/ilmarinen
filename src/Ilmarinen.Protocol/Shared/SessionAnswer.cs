namespace Ilmarinen.Protocol;

/// <summary>The server's answer to a worker's call on a session, as a value rather than an exception, so the worker can tell a refused session from a fault.</summary>
public enum SessionAnswer
{
    Ok,

    /// <summary>The connection isn't the worker's current one: a later connection superseded it, or it never got in. The worker reconnects.</summary>
    Fenced,

    /// <summary>The connection is current but hasn't sent Resume yet, so the server won't act on what the worker says.</summary>
    NotResumed
}
