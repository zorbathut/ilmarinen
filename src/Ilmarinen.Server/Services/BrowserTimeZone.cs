using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Services;

/// <summary>
/// The viewer's time zone, as their browser reports it. Scoped, so one per circuit: asked once, then remembered for every page the viewer moves to.
/// </summary>
public class BrowserTimeZone
{
    private readonly IJSRuntime _js;
    private readonly ILogger<BrowserTimeZone> _logger;
    private TimeZoneInfo? _zone;

    public BrowserTimeZone(IJSRuntime js, ILogger<BrowserTimeZone> logger)
    {
        _js = js;
        _logger = logger;
    }

    /// <summary>The zone once it's known, without asking; null before the first <see cref="GetAsync"/> answers, or if it couldn't.</summary>
    public TimeZoneInfo? Known
    {
        get
        {
            return _zone;
        }
    }

    /// <summary>
    /// Asks the browser, the first time; only callable once the page has rendered interactively (from OnAfterRenderAsync). Null when the browser's answer couldn't be used, so the page shows UTC.
    /// </summary>
    public async Task<TimeZoneInfo?> GetAsync()
    {
        if (_zone != null)
        {
            return _zone;
        }

        string? id = null;
        try
        {
            id = await _js.InvokeAsync<string?>("getBrowserTimeZone");
            // The browser's answer is untrusted: null, or an id this system doesn't know, throws.
            _zone = TimeZoneInfo.FindSystemTimeZoneById(id!);
        }
        catch (Exception ex) when (BrowserInterop.IsCircuitGone(ex))
        {
            _logger.LogDebug(ex, "The circuit went away while asking for the browser's time zone");
        }
        catch (Exception ex) when (ex is JSException or TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            _logger.LogWarning(ex, "The browser's time zone {ZoneId} couldn't be used; showing UTC", id);
        }
        return _zone;
    }
}
