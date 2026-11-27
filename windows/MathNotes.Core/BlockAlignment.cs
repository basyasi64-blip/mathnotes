namespace MathNotes.Core;

public static class BlockAlignment
{
    /// <summary>
    /// Manuelle Ausrichtung hat Vorrang. Automatisch: Schreibposition der Mitte des Blocks auf der Seite
    /// (linkes Drittel = links, rechtes Drittel = rechts, sonst Mitte).
    /// </summary>
    public static string Resolve(string? align, StrokeBox box, double pageWidth)
    {
        if (align is "left" or "center" or "right") return align;
        if (pageWidth <= 0) return "center";
        var f = (box.MinX + box.MaxX) / 2 / pageWidth;
        return f < 1.0 / 3 ? "left" : f > 2.0 / 3 ? "right" : "center";
    }
}
