using System.Text.Json.Nodes;

namespace MathNotes.Core;

/// <summary>Baut die Daten fuer die Vorlesungsansicht: Zeilen in Lesereihenfolge, je Block Typ, Ausrichtung, Inhalt.</summary>
public static class LectureBuilder
{
    public static string Build(
        IReadOnlyList<StrokeGroup> groups, IReadOnlyList<FormulaRecord> formulas, double pageWidth, double scale)
    {
        var rows = new JsonArray();
        JsonArray? current = null;
        JsonObject? last = null;
        var lastRow = int.MinValue;
        for (var i = 0; i < groups.Count && i < formulas.Count; i++)
        {
            var g = groups[i];
            var f = formulas[i];
            if (g.Row != lastRow || current is null)
            {
                current = new JsonArray();
                rows.Add(new JsonObject { ["blocks"] = current });
                lastRow = g.Row;
                last = null;
            }
            var content = f.CurrentLatex ?? "";
            var align = BlockAlignment.Resolve(f.Align, g.Box, pageWidth);
            var pending = string.IsNullOrWhiteSpace(content);

            // Woerter einer Handschrift-Zeile stehen oft als getrennte Bloecke: zu einer Textzeile verbinden
            if (last is not null && !pending && f.Kind == "text" &&
                last["kind"]!.GetValue<string>() == "text" &&
                last["align"]!.GetValue<string>() == align &&
                !last["pending"]!.GetValue<bool>())
            {
                last["content"] = last["content"]!.GetValue<string>() + " " + content;
                continue;
            }

            last = new JsonObject
            {
                ["kind"] = f.Kind,
                ["align"] = align,
                ["content"] = content,
                ["pending"] = pending,
            };
            current.Add(last);
        }
        return new JsonObject { ["scale"] = scale, ["rows"] = rows }.ToJsonString();
    }
}
