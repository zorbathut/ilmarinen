namespace Ilmarinen.Server.Services;

/// <summary>
/// A server-wide hold on dispatch, for draining the fleet before a deploy: while it is held no queued job is handed out and no sleeping worker is woken for one (a wake already under way runs its course), and what is running carries on. Held in memory, not stored, so the restart a deploy makes is what ends it. Set through <see cref="JobScheduler.SetDrainingAsync"/>, which takes the dispatch lock to do it.
/// </summary>
public sealed class DispatchHold
{
    private volatile bool _held;

    public bool Held
    {
        get { return _held; }
    }

    public void Set(bool held)
    {
        _held = held;
    }
}
