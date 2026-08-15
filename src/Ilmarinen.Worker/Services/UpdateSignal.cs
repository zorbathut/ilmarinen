namespace Ilmarinen.Worker.Services;

/// <summary>
/// Set by WorkerService when the process should exit with WorkerExitCodes.UpdateRequired. A singleton flag rather than Environment.Exit so the in-process integration tests can observe the decision.
/// </summary>
public class UpdateSignal
{
    private volatile bool _updateRequired;

    public bool UpdateRequired
    {
        get { return _updateRequired; }
        set { _updateRequired = value; }
    }
}
