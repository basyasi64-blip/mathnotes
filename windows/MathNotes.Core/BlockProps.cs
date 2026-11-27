using System.Text.Json.Nodes;

namespace MathNotes.Core;

/// <summary>
/// Darstellungs-Eigenschaften eines Blocks (als JSON in der Spalte props gespeichert):
/// Groesse (Faktor, 1 = normal), fett/kursiv/unterstrichen (nur Text) und Achsenbereiche (nur Graph; null = automatisch).
/// </summary>
public sealed record BlockProps(
    double Scale = 1.0, bool Bold = false, bool Italic = false, bool Underline = false,
    double? XMin = null, double? XMax = null, double? YMin = null, double? YMax = null)
{
    public static readonly double[] ScaleSteps = { 0.7, 0.85, 1.0, 1.3, 1.6, 2.0 };

    public static BlockProps Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new BlockProps();
        try
        {
            var o = JsonNode.Parse(json)?.AsObject();
            if (o is null) return new BlockProps();
            double? D(string k) => o[k] is JsonNode n && n.GetValueKind() == System.Text.Json.JsonValueKind.Number ? n.GetValue<double>() : null;
            bool B(string k) => o[k] is JsonNode n && n.GetValueKind() is System.Text.Json.JsonValueKind.True;
            var scale = D("scale") ?? 1.0;
            return new BlockProps(scale is > 0.2 and < 6 ? scale : 1.0, B("bold"), B("italic"), B("underline"),
                D("xmin"), D("xmax"), D("ymin"), D("ymax"));
        }
        catch
        {
            return new BlockProps();   // kaputtes JSON darf nie zum Absturz fuehren
        }
    }

    /// <summary>Nur Abweichungen vom Standard werden gespeichert. Standard = null.</summary>
    public string? ToJson()
    {
        var o = new JsonObject();
        if (Math.Abs(Scale - 1.0) > 1e-9) o["scale"] = Scale;
        if (Bold) o["bold"] = true;
        if (Italic) o["italic"] = true;
        if (Underline) o["underline"] = true;
        if (XMin is not null) o["xmin"] = XMin;
        if (XMax is not null) o["xmax"] = XMax;
        if (YMin is not null) o["ymin"] = YMin;
        if (YMax is not null) o["ymax"] = YMax;
        return o.Count == 0 ? null : o.ToJsonString();
    }

    public JsonObject ToNode()
    {
        var o = new JsonObject { ["scale"] = Scale, ["bold"] = Bold, ["italic"] = Italic, ["underline"] = Underline };
        if (XMin is not null) o["xmin"] = XMin;
        if (XMax is not null) o["xmax"] = XMax;
        if (YMin is not null) o["ymin"] = YMin;
        if (YMax is not null) o["ymax"] = YMax;
        return o;
    }
}
