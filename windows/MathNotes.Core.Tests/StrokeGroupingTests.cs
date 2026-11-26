using MathNotes.Core;
using Xunit;

namespace MathNotes.Core.Tests;

public class StrokeGroupingTests
{
    private static (long, StrokeData) Box(long id, double x, double y, double w, double h) =>
        (id, new StrokeData("#000", 2, new[] { new PointData(x, y, .5, 0), new PointData(x + w, y + h, .5, 1) }));

    private static List<List<long>> Ids(List<StrokeGroup> g) => g.Select(x => x.Ids.ToList()).ToList();

    [Fact]
    public void Leer_und_Einzelstrich()
    {
        Assert.Empty(StrokeGrouping.Group(new List<(long, StrokeData)>()));
        Assert.Equal(new[] { new List<long> { 1 } }, Ids(StrokeGrouping.Group(new[] { Box(1, 0, 0, 30, 40) })));
    }

    [Fact]
    public void FvonX_bleibt_eine_Formel()
    {
        var items = new[] { Box(1, 0, 0, 20, 40), Box(2, 28, 0, 12, 40), Box(3, 48, 10, 24, 30), Box(4, 80, 0, 12, 40) };
        Assert.Equal(new[] { new List<long> { 1, 2, 3, 4 } }, Ids(StrokeGrouping.Group(items)));
    }

    [Fact]
    public void Bruch_bleibt_eine_Formel()
    {
        var items = new[] { Box(1, 0, 0, 30, 40), Box(2, -10, 50, 60, 2), Box(3, 0, 62, 30, 40) };
        Assert.Equal(new[] { new List<long> { 1, 2, 3 } }, Ids(StrokeGrouping.Group(items)));
    }

    [Fact]
    public void Nebeneinander_links_nach_rechts()
    {
        var items = new[] { Box(3, 250, 0, 40, 40), Box(4, 298, 0, 40, 40), Box(1, 0, 0, 40, 40), Box(2, 48, 0, 40, 40) };
        Assert.Equal(new[] { new List<long> { 1, 2 }, new List<long> { 3, 4 } }, Ids(StrokeGrouping.Group(items)));
    }

    [Fact]
    public void Untereinander_oben_nach_unten()
    {
        var items = new[] { Box(3, 0, 100, 40, 40), Box(4, 48, 100, 40, 40), Box(1, 0, 0, 40, 40), Box(2, 48, 0, 40, 40) };
        Assert.Equal(new[] { new List<long> { 1, 2 }, new List<long> { 3, 4 } }, Ids(StrokeGrouping.Group(items)));
    }

    [Fact]
    public void Raster_2x2_Lesereihenfolge()
    {
        var items = new[] { Box(4, 300, 116, 40, 40), Box(3, 0, 120, 40, 40), Box(2, 300, 4, 40, 40), Box(1, 0, 0, 40, 40) };
        Assert.Equal(new[] { new List<long> { 1 }, new List<long> { 2 }, new List<long> { 3 }, new List<long> { 4 } },
                     Ids(StrokeGrouping.Group(items)));
    }

    [Fact]
    public void HochUndTiefgestellt_bleiben_zusammen()
    {
        var items = new[] { Box(1, 0, 0, 30, 40), Box(2, 34, -10, 15, 20), Box(3, 34, 35, 15, 20) };
        Assert.Equal(new[] { new List<long> { 1, 2, 3 } }, Ids(StrokeGrouping.Group(items)));
    }

    [Fact]
    public void Zeichengroesse_skaliert_mit_Schrift()
    {
        Assert.Single(StrokeGrouping.Group(new[] { Box(1, 0, 0, 80, 80), Box(2, 180, 0, 80, 80) }));
        Assert.Equal(2, StrokeGrouping.Group(new[] { Box(1, 0, 0, 20, 20), Box(2, 120, 0, 20, 20) }).Count);
    }
}
