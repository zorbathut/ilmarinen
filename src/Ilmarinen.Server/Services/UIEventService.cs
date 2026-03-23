using System;

namespace Ilmarinen.Server.Services;

/// <summary>
/// Simple in-process event bus for notifying Blazor pages of data changes.
/// Registered as a singleton so all pages share the same instance.
/// </summary>
public class UIEventService
{
    public event Action? OnJobsChanged;

    public void NotifyJobsChanged()
    {
        OnJobsChanged?.Invoke();
    }
}
