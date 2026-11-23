using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

namespace Ilmarinen.Server.Services;

/// <summary>
/// The viewer's saved table layouts, kept in their browser's localStorage. Scoped, so one per circuit: what it has read or written it remembers, so a table the viewer returns to renders their layout at once instead of the defaults first.
/// </summary>
public class TableLayoutStore
{
    private const string KeyPrefix = "ilmarinen.table.";

    private readonly IJSRuntime _js;
    private readonly ILogger<TableLayoutStore> _logger;

    /// <summary>A null value is known to be absent: the viewer has no saved layout for that table.</summary>
    private readonly Dictionary<string, string?> _known = [];

    public TableLayoutStore(IJSRuntime js, ILogger<TableLayoutStore> logger)
    {
        _js = js;
        _logger = logger;
    }

    /// <summary>The saved layout's text, if this circuit already knows it; <paramref name="json"/> is null when the viewer has none.</summary>
    public bool TryGetKnown(string table, out string? json)
    {
        return _known.TryGetValue(table, out json);
    }

    /// <summary>Reads the saved layout from the browser; only callable once the page has rendered interactively. Null when there is none, or it couldn't be read.</summary>
    public async Task<string?> LoadAsync(string table)
    {
        try
        {
            var json = await _js.InvokeAsync<string?>("localStorage.getItem", KeyPrefix + table);
            // A save or reset while the read was on its way is newer than what it read.
            _known.TryAdd(table, json);
            return _known[table];
        }
        catch (Exception ex) when (BrowserInterop.IsCircuitGone(ex))
        {
            _logger.LogDebug(ex, "The circuit went away while reading the layout of table {Table}", table);
        }
        catch (JSException ex)
        {
            _logger.LogWarning(ex, "Reading the saved layout of table {Table} failed; showing the defaults", table);
        }
        return null;
    }

    public async Task SaveAsync(string table, string json)
    {
        _known[table] = json;
        await WriteAsync(table, "localStorage.setItem", KeyPrefix + table, json);
    }

    /// <summary>Forgets the saved layout, so the table follows its defaults, including any later change to them.</summary>
    public async Task RemoveAsync(string table)
    {
        _known[table] = null;
        await WriteAsync(table, "localStorage.removeItem", KeyPrefix + table);
    }

    private async Task WriteAsync(string table, string function, params object[] args)
    {
        try
        {
            await _js.InvokeVoidAsync(function, args);
        }
        catch (Exception ex) when (BrowserInterop.IsCircuitGone(ex))
        {
            _logger.LogDebug(ex, "The circuit went away while saving the layout of table {Table}", table);
        }
        catch (JSException ex)
        {
            _logger.LogWarning(ex, "Saving the layout of table {Table} failed; it lasts until the page reloads", table);
        }
    }
}
