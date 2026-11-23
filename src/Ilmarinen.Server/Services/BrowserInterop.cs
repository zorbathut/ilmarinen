using Microsoft.JSInterop;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Services;

public static class BrowserInterop
{
    /// <summary>Whether a JS interop call failed because the viewer's circuit went away (they left or closed the page), which is routine, not a fault.</summary>
    public static bool IsCircuitGone(Exception ex)
    {
        return ex is JSDisconnectedException or ObjectDisposedException or TaskCanceledException;
    }
}
