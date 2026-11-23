using Microsoft.AspNetCore.Components;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System;

namespace Ilmarinen.Server.Components;

/// <summary>What a table needs to know about a column to arrange it, whatever its rows are.</summary>
public interface ITableColumn
{
    /// <summary>Stable across releases: it is what a viewer's saved layout names.</summary>
    string Id { get; }
    string Title { get; }
    bool ShownByDefault { get; }
    bool Sortable { get; }
    bool FirstDescending { get; }
    bool HideMobile { get; }
}

/// <summary>
/// One column a table can show. <see cref="Cell"/> renders the cell's content; the table owns the <c>td</c>.
/// </summary>
public record TableColumn<TItem>(string Id, string Title, RenderFragment<TItem> Cell) : ITableColumn
{
    /// <summary>What the column sorts by; null for a column that can't be sorted. Every row's key must be of one type (or null, which sorts last).</summary>
    public Func<TItem, IComparable?>? SortKey { get; init; }

    /// <summary>Whether the first click sorts descending: newest or largest first, for dates and sizes.</summary>
    public bool FirstDescending { get; init; }

    /// <summary>Whether the column shows until the viewer says otherwise.</summary>
    public bool ShownByDefault { get; init; } = true;

    /// <summary>Whether the default layout drops the column on a narrow screen.</summary>
    public bool HideMobile { get; init; }

    public bool Sortable
    {
        get
        {
            return SortKey != null;
        }
    }
}

public sealed record TableSort(string Column, bool Descending);

/// <summary>
/// How a viewer has arranged a table: the order of every column in its catalog, which are hidden, and what it is sorted by. Immutable; every change gives a new layout.
/// </summary>
public sealed record TableLayout
{
    /// <summary>Every catalog column, in display order, hidden ones included so they keep their place.</summary>
    public required IReadOnlyList<string> Order { get; init; }
    public required IReadOnlySet<string> Hidden { get; init; }

    /// <summary>Null keeps the rows in the order they were given.</summary>
    public TableSort? Sort { get; init; }

    /// <summary>Whether the viewer has moved, shown or hidden columns, rather than seeing the default ones. Sorting doesn't count.</summary>
    public bool ColumnsChosen { get; init; }

    public static TableLayout Defaults(IReadOnlyList<ITableColumn> catalog)
    {
        return new TableLayout
        {
            Order = catalog.Select(c => c.Id).ToList(),
            Hidden = catalog.Where(c => !c.ShownByDefault).Select(c => c.Id).ToHashSet(),
        };
    }

    /// <summary>
    /// Reads a saved layout against today's catalog. Whatever the stored text, the result is a usable layout or null: null when it isn't a layout at all, so the caller can say so and use the defaults. Within a layout, a member of the wrong type reads as absent, unknown and repeated ids are dropped, a column the saved layout lacks goes after its catalog predecessor at its default visibility, and a sort that can't apply is dropped. A layout whose columns the viewer never chose takes today's default columns, so it follows a release that changes them.
    /// </summary>
    public static TableLayout? Parse(IReadOnlyList<ITableColumn> catalog, string json)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var columnsChosen = root.TryGetProperty("columnsChosen", out var chosen) && chosen.ValueKind == JsonValueKind.True;
        var known = columnsChosen ? catalog.Select(c => c.Id).ToHashSet() : [];
        var order = Strings(root, "order").Where(known.Contains).Distinct().ToList();
        var hidden = Strings(root, "hidden").Where(known.Contains).Where(order.Contains).ToHashSet();

        for (var i = 0; i < catalog.Count; i++)
        {
            var column = catalog[i];
            if (order.Contains(column.Id))
            {
                continue;
            }
            var after = i == 0 ? -1 : order.IndexOf(catalog[i - 1].Id);
            order.Insert(after + 1, column.Id);
            if (!column.ShownByDefault)
            {
                hidden.Add(column.Id);
            }
        }

        if (order.All(hidden.Contains))
        {
            hidden = Defaults(catalog).Hidden.ToHashSet();
        }

        TableSort? sort = null;
        if (root.TryGetProperty("sort", out var sortElement)
            && sortElement.ValueKind == JsonValueKind.Object
            && sortElement.TryGetProperty("column", out var sortColumn)
            && sortColumn.ValueKind == JsonValueKind.String
            && catalog.FirstOrDefault(c => c.Id == sortColumn.GetString()) is { Sortable: true } sorted)
        {
            var descending = sortElement.TryGetProperty("descending", out var d) && d.ValueKind == JsonValueKind.True;
            sort = new TableSort(sorted.Id, descending);
        }

        return new TableLayout { Order = order, Hidden = hidden, Sort = sort, ColumnsChosen = columnsChosen };
    }

    private static IEnumerable<string> Strings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
    }

    public string Serialize()
    {
        return JsonSerializer.Serialize(new
        {
            order = Order,
            hidden = Order.Where(Hidden.Contains),
            sort = Sort == null ? null : new { column = Sort.Column, descending = Sort.Descending },
            columnsChosen = ColumnsChosen,
        });
    }

    /// <summary>The shown columns, in order.</summary>
    public IReadOnlyList<TColumn> Visible<TColumn>(IReadOnlyList<TColumn> catalog) where TColumn : ITableColumn
    {
        return Order.Where(id => !Hidden.Contains(id)).Select(id => catalog.FirstOrDefault(c => c.Id == id)).OfType<TColumn>().ToList();
    }

    /// <summary>Moves a column to <paramref name="toIndex"/> in <see cref="Order"/>, clamped to the ends.</summary>
    public TableLayout Move(string id, int toIndex)
    {
        if (!Order.Contains(id))
        {
            return this;
        }
        var order = Order.Where(o => o != id).ToList();
        order.Insert(Math.Clamp(toIndex, 0, order.Count), id);
        if (order.SequenceEqual(Order))
        {
            return this;
        }
        return this with { Order = order, ColumnsChosen = true };
    }

    public TableLayout Show(string id)
    {
        if (!Hidden.Contains(id))
        {
            return this;
        }
        return this with { Hidden = Hidden.Where(h => h != id).ToHashSet(), ColumnsChosen = true };
    }

    /// <summary>A table always keeps one column, so hiding the last shown one is refused.</summary>
    public bool CanHide(string id)
    {
        return Order.Any(o => o != id && !Hidden.Contains(o));
    }

    public TableLayout Hide(string id)
    {
        if (Hidden.Contains(id) || !CanHide(id))
        {
            return this;
        }
        // Rows in the order of a column no longer shown would have nothing saying why.
        var sort = Sort?.Column == id ? null : Sort;
        return this with { Hidden = Hidden.Append(id).ToHashSet(), Sort = sort, ColumnsChosen = true };
    }

    /// <summary>A column's first click sorts it in its first direction, the second the other way, the third turns sorting off.</summary>
    public TableLayout ToggleSort(string id, IReadOnlyList<ITableColumn> catalog)
    {
        if (catalog.FirstOrDefault(c => c.Id == id) is not { Sortable: true } column)
        {
            return this;
        }
        TableSort? sort;
        if (Sort?.Column != id)
        {
            sort = new TableSort(id, column.FirstDescending);
        }
        else if (Sort.Descending == column.FirstDescending)
        {
            sort = new TableSort(id, !column.FirstDescending);
        }
        else
        {
            sort = null;
        }
        return this with { Sort = sort };
    }

    /// <summary>The rows in this layout's sort, stable among equal keys, with missing keys last either way.</summary>
    public IEnumerable<TItem> Apply<TItem>(IEnumerable<TItem> items, IReadOnlyList<TableColumn<TItem>> catalog)
    {
        if (Sort == null || catalog.FirstOrDefault(c => c.Id == Sort.Column)?.SortKey is not { } key)
        {
            return items;
        }
        var keyed = items.Select(item => (Item: item, Key: key(item)));
        var missingLast = keyed.OrderBy(k => k.Key == null);
        var sorted = Sort.Descending ? missingLast.ThenByDescending(k => k.Key) : missingLast.ThenBy(k => k.Key);
        return sorted.Select(k => k.Item);
    }

    /// <summary>A column marked for narrow screens drops out of the default columns there; once the viewer has chosen columns, they see what they chose.</summary>
    public bool HidesOnMobile(ITableColumn column)
    {
        return column.HideMobile && !ColumnsChosen;
    }
}

public static class TableColumns
{
    /// <summary>
    /// A catalog arranged for one page: <paramref name="defaults"/> shown first, in that order, and every other column after them, hidden until the viewer adds it. An id the catalog lacks is a bug in the page, so it throws.
    /// </summary>
    public static IReadOnlyList<TableColumn<TItem>> ShownFirst<TItem>(IReadOnlyList<TableColumn<TItem>> catalog, IReadOnlyList<string> defaults)
    {
        var unknown = defaults.Where(id => catalog.All(c => c.Id != id)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"No such column: {string.Join(", ", unknown)}", nameof(defaults));
        }
        return defaults.Select(id => catalog.Single(c => c.Id == id) with { ShownByDefault = true })
            .Concat(catalog.Where(c => !defaults.Contains(c.Id)).Select(c => c with { ShownByDefault = false }))
            .ToList();
    }
}
