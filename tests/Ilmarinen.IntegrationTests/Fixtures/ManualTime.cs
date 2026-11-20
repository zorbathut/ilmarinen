using System;

namespace Ilmarinen.IntegrationTests.Fixtures;

/// <summary>A clock that moves only when a test moves it. Starts at the real time, so it agrees with what the server stamps by the real clock (a worker's LastSeen, a job's CreatedAt) until it is moved on.</summary>
public sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow()
    {
        return _now;
    }

    public void Advance(TimeSpan by)
    {
        _now += by;
    }
}
