using Ilmarinen.Server.Components;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System;

namespace Ilmarinen.IntegrationTests.Tests;

[TestFixture]
public class TableLayoutTests
{
    private record Row(string Name, int? Size, DateTime When);

    private static TableColumn<Row> Column(string id, bool shown = true, Func<Row, IComparable?>? sortKey = null, bool firstDescending = false, bool hideMobile = false)
    {
        return new TableColumn<Row>(id, id.ToUpperInvariant(), row => builder => { })
        {
            ShownByDefault = shown,
            SortKey = sortKey,
            FirstDescending = firstDescending,
            HideMobile = hideMobile,
        };
    }

    private static readonly IReadOnlyList<TableColumn<Row>> Catalog =
    [
        Column("name", sortKey: r => r.Name),
        Column("size", sortKey: r => r.Size, firstDescending: true, hideMobile: true),
        Column("when", sortKey: r => r.When, firstDescending: true),
        Column("extra", shown: false, sortKey: r => r.Name),
        Column("actions"),
    ];

    private static IReadOnlyList<string> VisibleIds(TableLayout layout)
    {
        return layout.Visible(Catalog).Select(c => c.Id).ToList();
    }

    private static TableLayout Parse(string json)
    {
        var layout = TableLayout.Parse(Catalog, json);
        Assert.That(layout, Is.Not.Null, json);
        return layout!;
    }

    [Test]
    public void Defaults_ShowTheShownColumnsInCatalogOrder_Unsorted()
    {
        var layout = TableLayout.Defaults(Catalog);

        Assert.That(layout.Order, Is.EqualTo(new[] { "name", "size", "when", "extra", "actions" }));
        Assert.That(VisibleIds(layout), Is.EqualTo(new[] { "name", "size", "when", "actions" }));
        Assert.That(layout.Sort, Is.Null);
        Assert.That(layout.ColumnsChosen, Is.False);
    }

    [Test]
    public void Serialize_ThenParse_RoundTrips()
    {
        var layout = TableLayout.Defaults(Catalog).Move("when", 0).Show("extra").Hide("size").ToggleSort("name", Catalog).ToggleSort("name", Catalog);

        var parsed = Parse(layout.Serialize());

        Assert.That(parsed.Order, Is.EqualTo(layout.Order));
        Assert.That(parsed.Hidden, Is.EquivalentTo(layout.Hidden));
        Assert.That(parsed.Sort, Is.EqualTo(new TableSort("name", Descending: true)));
        Assert.That(parsed.ColumnsChosen, Is.True);
        Assert.That(Parse(TableLayout.Defaults(Catalog).ToggleSort("name", Catalog).Serialize()).ColumnsChosen, Is.False);
    }

    [Test]
    public void Parse_KeepsASavedOrder()
    {
        var layout = Parse("""{"order":["actions","when","name","size","extra"],"hidden":["extra"],"columnsChosen":true}""");

        Assert.That(VisibleIds(layout), Is.EqualTo(new[] { "actions", "when", "name", "size" }));
    }

    [Test]
    public void Parse_DropsDuplicateAndUnknownIds()
    {
        var layout = Parse("""{"order":["when","when","gone","name","size","extra","actions","name"],"hidden":["gone","extra"],"columnsChosen":true}""");

        Assert.That(layout.Order, Is.EqualTo(new[] { "when", "name", "size", "extra", "actions" }));
        Assert.That(layout.Hidden, Is.EquivalentTo(new[] { "extra" }));
    }

    [Test]
    public void Parse_InsertsAColumnTheSavedLayoutLacks_AfterItsCatalogPredecessor_AtItsDefaultVisibility()
    {
        var layout = Parse("""{"order":["actions","name","when"],"hidden":[],"columnsChosen":true}""");

        Assert.That(layout.Order, Is.EqualTo(new[] { "actions", "name", "size", "when", "extra" }));
        Assert.That(VisibleIds(layout), Is.EqualTo(new[] { "actions", "name", "size", "when" }));
    }

    [Test]
    public void Parse_AColumnFirstInTheCatalog_GoesFirst()
    {
        var layout = Parse("""{"order":["size","when","extra","actions"],"hidden":[],"columnsChosen":true}""");

        Assert.That(layout.Order.First(), Is.EqualTo("name"));
    }

    [TestCase("""{"order":["name"],"sort":{"column":"gone","descending":false}}""")]
    [TestCase("""{"order":["name"],"sort":{"column":"actions","descending":false}}""")]
    [TestCase("""{"order":["name"],"sort":{"column":null}}""")]
    [TestCase("""{"order":["name"],"sort":"name"}""")]
    public void Parse_DropsASortItCanNotApply(string json)
    {
        Assert.That(Parse(json).Sort, Is.Null);
    }

    [TestCase("""{"order":null,"hidden":"x","sort":7}""")]
    [TestCase("""{"order":[1,true,null],"hidden":[{}]}""")]
    [TestCase("""{}""")]
    public void Parse_TreatsMissingOrWronglyTypedMembersAsAbsent(string json)
    {
        var layout = Parse(json);

        Assert.That(layout.Order, Is.EqualTo(TableLayout.Defaults(Catalog).Order));
        Assert.That(VisibleIds(layout), Is.EqualTo(VisibleIds(TableLayout.Defaults(Catalog))));
        Assert.That(layout.Sort, Is.Null);
    }

    [Test]
    public void Parse_ColumnsNeverChosen_TakesTodaysDefaultColumns_AndKeepsTheSort()
    {
        var layout = Parse("""{"order":["actions","name"],"hidden":["name"],"sort":{"column":"when","descending":true},"columnsChosen":false}""");

        Assert.That(layout.Order, Is.EqualTo(TableLayout.Defaults(Catalog).Order));
        Assert.That(VisibleIds(layout), Is.EqualTo(VisibleIds(TableLayout.Defaults(Catalog))));
        Assert.That(layout.Sort, Is.EqualTo(new TableSort("when", Descending: true)));
    }

    [Test]
    public void Parse_EveryColumnHidden_GivesTheDefaults()
    {
        var layout = Parse("""{"order":["name","size","when","extra","actions"],"hidden":["name","size","when","extra","actions"],"columnsChosen":true}""");

        Assert.That(VisibleIds(layout), Is.EqualTo(VisibleIds(TableLayout.Defaults(Catalog))));
    }

    [TestCase("")]
    [TestCase("{not json")]
    [TestCase("[]")]
    [TestCase("\"x\"")]
    [TestCase("null")]
    [TestCase("42")]
    public void Parse_NotALayout_IsNull(string json)
    {
        Assert.That(TableLayout.Parse(Catalog, json), Is.Null);
    }

    [TestCase("when", 0, new[] { "when", "name", "size", "extra", "actions" })]
    [TestCase("name", 4, new[] { "size", "when", "extra", "actions", "name" })]
    [TestCase("name", 99, new[] { "size", "when", "extra", "actions", "name" })]
    [TestCase("actions", -3, new[] { "actions", "name", "size", "when", "extra" })]
    public void Move_PutsTheColumnAtTheIndex_Clamped(string id, int toIndex, string[] expected)
    {
        var layout = TableLayout.Defaults(Catalog).Move(id, toIndex);

        Assert.That(layout.Order, Is.EqualTo(expected));
        Assert.That(layout.ColumnsChosen, Is.True);
    }

    [Test]
    public void Move_AnUnknownColumnOrOntoItsOwnPlace_ChangesNothing()
    {
        var layout = TableLayout.Defaults(Catalog);

        Assert.That(layout.Move("gone", 0).Order, Is.EqualTo(layout.Order));
        Assert.That(layout.Move("size", 1).ColumnsChosen, Is.False);
    }

    [Test]
    public void ShowAndHide_AreIdempotent()
    {
        var layout = TableLayout.Defaults(Catalog);

        Assert.That(VisibleIds(layout.Show("extra").Show("extra")), Is.EqualTo(new[] { "name", "size", "when", "extra", "actions" }));
        Assert.That(VisibleIds(layout.Hide("size").Hide("size")), Is.EqualTo(new[] { "name", "when", "actions" }));
        Assert.That(layout.Show("extra").ColumnsChosen, Is.True);
        Assert.That(layout.Show("name").ColumnsChosen, Is.False);
        Assert.That(layout.Hide("size").Hide("size").ColumnsChosen, Is.True);
    }

    [Test]
    public void Hide_TheSortedColumn_StopsSortingByIt()
    {
        var layout = TableLayout.Defaults(Catalog).ToggleSort("name", Catalog);

        Assert.That(layout.Hide("name").Sort, Is.Null);
        Assert.That(layout.Hide("size").Sort, Is.EqualTo(layout.Sort));
    }

    [Test]
    public void Hide_TheLastShownColumn_IsRefused()
    {
        var layout = TableLayout.Defaults(Catalog).Hide("name").Hide("size").Hide("when");

        Assert.That(VisibleIds(layout.Hide("actions")), Is.EqualTo(new[] { "actions" }));
        Assert.That(layout.CanHide("actions"), Is.False);
        Assert.That(TableLayout.Defaults(Catalog).CanHide("actions"), Is.True);
    }

    [Test]
    public void ToggleSort_CyclesFromTheColumnsFirstDirection_ThroughTheOther_ToOff()
    {
        var layout = TableLayout.Defaults(Catalog);

        var once = layout.ToggleSort("name", Catalog);
        var twice = once.ToggleSort("name", Catalog);
        var thrice = twice.ToggleSort("name", Catalog);

        Assert.That(once.Sort, Is.EqualTo(new TableSort("name", Descending: false)));
        Assert.That(twice.Sort, Is.EqualTo(new TableSort("name", Descending: true)));
        Assert.That(thrice.Sort, Is.Null);
        Assert.That(layout.ToggleSort("when", Catalog).Sort, Is.EqualTo(new TableSort("when", Descending: true)));
    }

    [Test]
    public void ToggleSort_OnAnotherColumn_StartsItInItsFirstDirection()
    {
        var layout = TableLayout.Defaults(Catalog).ToggleSort("name", Catalog).ToggleSort("name", Catalog);

        Assert.That(layout.ToggleSort("size", Catalog).Sort, Is.EqualTo(new TableSort("size", Descending: true)));
        Assert.That(layout.ToggleSort("extra", Catalog).Sort, Is.EqualTo(new TableSort("extra", Descending: false)));
    }

    [Test]
    public void ToggleSort_OnAnUnsortableColumn_ChangesNothing()
    {
        var layout = TableLayout.Defaults(Catalog).ToggleSort("name", Catalog);

        Assert.That(layout.ToggleSort("actions", Catalog).Sort, Is.EqualTo(layout.Sort));
    }

    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyList<Row> Rows =
    [
        new("b", 2, T0),
        new("a", null, T0.AddHours(1)),
        new("c", 1, T0.AddHours(2)),
        new("a", 3, T0.AddHours(3)),
    ];

    private static IReadOnlyList<Row> Sorted(string column, bool descending)
    {
        var layout = TableLayout.Defaults(Catalog) with { Sort = new TableSort(column, descending) };
        return layout.Apply(Rows, Catalog).ToList();
    }

    [Test]
    public void Apply_Unsorted_KeepsTheGivenOrder()
    {
        Assert.That(TableLayout.Defaults(Catalog).Apply(Rows, Catalog), Is.EqualTo(Rows));
    }

    [Test]
    public void Apply_SortsBothWays_StablyAmongEqualKeys()
    {
        Assert.That(Sorted("name", descending: false), Is.EqualTo(new[] { Rows[1], Rows[3], Rows[0], Rows[2] }));
        Assert.That(Sorted("name", descending: true), Is.EqualTo(new[] { Rows[2], Rows[0], Rows[1], Rows[3] }));
    }

    [Test]
    public void Apply_PutsMissingKeysLast_EitherWay()
    {
        Assert.That(Sorted("size", descending: false), Is.EqualTo(new[] { Rows[2], Rows[0], Rows[3], Rows[1] }));
        Assert.That(Sorted("size", descending: true), Is.EqualTo(new[] { Rows[3], Rows[0], Rows[2], Rows[1] }));
    }

    [Test]
    public void HidesOnMobile_OnlyOnTheDefaultLayout()
    {
        var size = Catalog.Single(c => c.Id == "size");
        var name = Catalog.Single(c => c.Id == "name");

        Assert.That(TableLayout.Defaults(Catalog).HidesOnMobile(size), Is.True);
        Assert.That(TableLayout.Defaults(Catalog).HidesOnMobile(name), Is.False);
        Assert.That(TableLayout.Defaults(Catalog).Show("extra").HidesOnMobile(size), Is.False);
        Assert.That(Parse(TableLayout.Defaults(Catalog).Move("when", 0).Serialize()).HidesOnMobile(size), Is.False);
    }

    [Test]
    public void HidesOnMobile_SortingIsNotChoosingColumns()
    {
        var size = Catalog.Single(c => c.Id == "size");
        var sorted = TableLayout.Defaults(Catalog).ToggleSort("name", Catalog);

        Assert.That(sorted.HidesOnMobile(size), Is.True);
        Assert.That(Parse(sorted.Serialize()).HidesOnMobile(size), Is.True);
    }

    [Test]
    public void ShownFirst_PutsTheDefaultsFirstInTheirOrder_AndHidesTheRest()
    {
        var arranged = TableColumns.ShownFirst(Catalog, ["when", "name"]);

        Assert.That(arranged.Select(c => c.Id), Is.EqualTo(new[] { "when", "name", "size", "extra", "actions" }));
        Assert.That(arranged.Where(c => c.ShownByDefault).Select(c => c.Id), Is.EqualTo(new[] { "when", "name" }));
    }

    [Test]
    public void ShownFirst_AnUnknownDefault_Throws()
    {
        Assert.Throws<ArgumentException>(() => TableColumns.ShownFirst(Catalog, ["gone"]));
    }
}
