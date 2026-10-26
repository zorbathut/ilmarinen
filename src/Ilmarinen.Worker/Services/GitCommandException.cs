using System;

namespace Ilmarinen.Worker.Services;

/// <summary>
/// The git command-line tool failed; the message carries its exit code and what it printed to stderr.
/// </summary>
public class GitCommandException : Exception
{
    public GitCommandException(string message)
        : base(message)
    {
    }
}
