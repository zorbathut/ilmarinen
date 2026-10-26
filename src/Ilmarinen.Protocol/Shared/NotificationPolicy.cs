namespace Ilmarinen.Protocol;

/// <summary>
/// Which of a pipeline's job completions are announced to notification subscribers. The values cross the API as integers, so they are fixed.
/// </summary>
public enum NotificationPolicy
{
    All = 0,

    /// <summary>Only jobs that failed. A cancellation is somebody's deliberate act, so it doesn't count.</summary>
    FailuresOnly = 1,

    None = 2
}
