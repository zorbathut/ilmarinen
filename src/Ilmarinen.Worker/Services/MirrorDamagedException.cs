using System;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// A repository mirror failed its own integrity check, so whatever failed while reading it is the mirror's fault and a rebuild can fix it.
/// </summary>
public class MirrorDamagedException : Exception
{
    public MirrorDamagedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
