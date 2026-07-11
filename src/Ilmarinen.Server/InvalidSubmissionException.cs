using System;
namespace Ilmarinen.Server;

/// <summary>
/// Thrown when a job submission fails validation (unknown pipeline, unresolvable repo/ref/script).
/// Results in HTTP 400 Bad Request with the validation message.
/// </summary>
public class InvalidSubmissionException : Exception
{
    public InvalidSubmissionException(string message) : base(message) { }
}
