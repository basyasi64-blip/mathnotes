using System.Text.Json.Nodes;

namespace MathNotes.Core;

/// <summary>Strichformat, identisch mit dem Python-Kern: {"c":..,"w":..,"p":[[x,y,druck,t_ms],..]}</summary>
public static class StrokeJson
{
    public static JsonObject ToNode(StrokeData s)
    {
        var pts = new JsonArray();
        foreach (var p in s.Points)
            pts.Add(new JsonArray(p.X, p.Y, p.Pressure, p.TimeMs));
        return new JsonObject { ["c"] = s.Color, ["w"] = s.Width, ["p"] = pts };
    }

    public static string Encode(StrokeData s) => ToNode(s).ToJsonString();

    public static StrokeData Decode(string json)
    {
        var o = JsonNode.Parse(json)!.AsObject();
        var pts = o["p"]!.AsArray().Select(a =>
        {
            var x = a!.AsArray();
            return new PointData(x[0]!.GetValue<double>(), x[1]!.GetValue<double>(),
                                 x[2]!.GetValue<double>(), x[3]!.GetValue<long>());
        }).ToList();
        return new StrokeData(o["c"]!.GetValue<string>(), o["w"]!.GetValue<double>(), pts);
    }
}
