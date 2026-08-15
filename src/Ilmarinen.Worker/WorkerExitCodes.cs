namespace Ilmarinen.Worker;

public static class WorkerExitCodes
{
    /// <summary>
    /// The worker is running under the launcher and wants to be relaunched from the server's current bundle. The launcher duplicates this constant (it deliberately references no Ilmarinen projects); the two values must match.
    /// </summary>
    public const int UpdateRequired = 42;
}
