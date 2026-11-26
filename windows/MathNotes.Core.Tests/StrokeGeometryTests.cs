using MathNotes.Core;
using Xunit;

namespace MathNotes.Core.Tests;

public class StrokeGeometryTests
{
    private static readonly (double, double)[] Line = { (0, 0), (100, 0) };

    [Fact] public void Trifft_NaheAmSegment() => Assert.True(StrokeGeometry.Hits(Line, 50, 5, 10));
    [Fact] public void Trifft_NichtWeitWeg() => Assert.False(StrokeGeometry.Hits(Line, 50, 30, 10));
    [Fact] public void Trifft_NahAmEndpunkt() => Assert.True(StrokeGeometry.Hits(Line, 105, 0, 10));
    [Fact] public void Trifft_NichtJenseitsDesEndpunkts() => Assert.False(StrokeGeometry.Hits(Line, 120, 0, 10));
    [Fact] public void Einzelpunkt() { Assert.True(StrokeGeometry.Hits(new[] { (5.0, 5.0) }, 8, 9, 6)); Assert.False(StrokeGeometry.Hits(new[] { (5.0, 5.0) }, 50, 50, 6)); }
    [Fact] public void LeererStrich_TrifftNie() => Assert.False(StrokeGeometry.Hits(Array.Empty<(double, double)>(), 0, 0, 100));
}
