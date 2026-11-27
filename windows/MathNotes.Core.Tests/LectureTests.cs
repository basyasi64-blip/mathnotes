using System.Text.Json.Nodes;
using MathNotes.Core;
using Xunit;

namespace MathNotes.Core.Tests;

public class LectureTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"mn_{Guid.NewGuid():N}.db");
    private static StrokeBox Box(double x0, double x1) => new(x0, 0, x1, 40);
    private static IReadOnlyList<IReadOnlyList<long>> G(params long[][] g) => g.Select(x => (IReadOnlyList<long>)x).ToList();

    [Fact]
    public void Ausrichtung_automatisch_nach_Schreibposition()
    {
        Assert.Equal("left", BlockAlignment.Resolve(null, Box(10, 100), 900));
        Assert.Equal("center", BlockAlignment.Resolve(null, Box(350, 550), 900));
        Assert.Equal("right", BlockAlignment.Resolve(null, Box(700, 880), 900));
        Assert.Equal("center", BlockAlignment.Resolve(null, Box(0, 10), 0));
    }

    [Fact]
    public void Ausrichtung_manuell_hat_Vorrang()
    {
        Assert.Equal("right", BlockAlignment.Resolve("right", Box(10, 100), 900));
        Assert.Equal("left", BlockAlignment.Resolve("left", Box(700, 880), 900));
        Assert.Equal("left", BlockAlignment.Resolve("unbekannt", Box(10, 100), 900));   // ungueltig -> automatisch
    }

    [Fact]
    public void Vorlesung_Zeilen_Typen_und_Ausrichtung()
    {
        using var s = new NotebookStore(TempDb());
        var page = s.FirstPage(s.CreateNotebook("x"));
        var recs = s.SyncFormulas(page, G(new long[] { 1 }, new long[] { 2 }, new long[] { 3 }), "formula");
        s.SetKind(recs[2].Id, "text");
        s.SaveRecognitionFor(recs[0].Id, new long[] { 1 }, new RecognitionOutcome("e", "1", new[] { new RecognitionCandidate("a^2", null) }, null));
        s.EditFormula(recs[1].Id, "b^2");
        s.SetAlign(recs[1].Id, "center");
        var formulas = s.Formulas(page);
        var groups = new List<StrokeGroup>
        {
            new(new long[] { 1 }, Box(10, 100), 0),
            new(new long[] { 2 }, Box(700, 880), 0),
            new(new long[] { 3 }, Box(10, 400), 1),
        };
        var json = JsonNode.Parse(LectureBuilder.Build(groups, formulas, 900, 1.5))!.AsObject();
        Assert.Equal(1.5, json["scale"]!.GetValue<double>());
        var rows = json["rows"]!.AsArray();
        Assert.Equal(2, rows.Count);
        var r0 = rows[0]!["blocks"]!.AsArray();
        Assert.Equal(2, r0.Count);
        Assert.Equal("left", r0[0]!["align"]!.GetValue<string>());
        Assert.Equal("a^2", r0[0]!["content"]!.GetValue<string>());
        Assert.Equal("center", r0[1]!["align"]!.GetValue<string>());   // manuell, obwohl rechts geschrieben
        Assert.Equal("b^2", r0[1]!["content"]!.GetValue<string>());
        var b = rows[1]!["blocks"]!.AsArray()[0]!;
        Assert.Equal("text", b["kind"]!.GetValue<string>());
        Assert.True(b["pending"]!.GetValue<bool>());                  // noch nicht erkannt
    }

    [Fact]
    public void NeueBloecke_uebernehmen_Schreibmodus_und_Typwechsel_leert_Inhalt_behaelt_Verlauf()
    {
        using var s = new NotebookStore(TempDb());
        var page = s.FirstPage(s.CreateNotebook("x"));
        var f = s.SyncFormulas(page, G(new long[] { 1 }), "text")[0];
        Assert.Equal("text", f.Kind);
        s.EditFormula(f.Id, "Hallo");
        s.SetKind(f.Id, "formula");
        var after = s.GetFormula(f.Id)!;
        Assert.Equal("formula", after.Kind);
        Assert.Null(after.EditedLatex);
        Assert.True(after.NeedsRecognition);
        Assert.Equal(2, s.FormulaVersionCount(page));                // 'Hallo' + Typwechsel bleiben nachvollziehbar
        Assert.Throws<ArgumentException>(() => s.SetKind(f.Id, "unsinn"));
        Assert.Throws<ArgumentException>(() => s.SetAlign(f.Id, "oben"));
    }

    [Fact]
    public void Gruppierung_liefert_Zeilenindex()
    {
        (long, StrokeData) B(long id, double x, double y) =>
            (id, new StrokeData("#000", 2, new[] { new PointData(x, y, .5, 0), new PointData(x + 40, y + 40, .5, 1) }));
        var g = StrokeGrouping.Group(new[] { B(1, 0, 0), B(2, 300, 3), B(3, 0, 120) });
        Assert.Equal(new[] { 0, 0, 1 }, g.Select(x => x.Row).ToArray());
    }

    [Fact]
    public void Woerter_einer_Zeile_werden_zu_einer_Textzeile()
    {
        using var s = new NotebookStore(TempDb());
        var page = s.FirstPage(s.CreateNotebook("x"));
        var recs = s.SyncFormulas(page, G(new long[] { 1 }, new long[] { 2 }, new long[] { 3 }, new long[] { 4 }), "text");
        s.EditFormula(recs[0].Id, "Wir fangen");
        s.EditFormula(recs[1].Id, "mit");
        s.EditFormula(recs[2].Id, "einer");
        s.SetKind(recs[3].Id, "formula");
        s.EditFormula(recs[3].Id, "x^2");
        var groups = new List<StrokeGroup>
        {
            // Seitenbreite 900: Mitten bei 55, 150 und 235 liegen im linken Drittel (alle links), 485 liegt in der Mitte
            new(new long[] { 1 }, Box(10, 100), 0), new(new long[] { 2 }, Box(120, 180), 0),
            new(new long[] { 3 }, Box(190, 280), 0), new(new long[] { 4 }, Box(450, 520), 0),
        };
        var rows = JsonNode.Parse(LectureBuilder.Build(groups, s.Formulas(page), 900, 1))!["rows"]!.AsArray();
        var blocks = rows[0]!["blocks"]!.AsArray();
        Assert.Equal(2, blocks.Count);                                              // Textzeile + Formel
        Assert.Equal("Wir fangen mit einer", blocks[0]!["content"]!.GetValue<string>());
        Assert.Equal("x^2", blocks[1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Props_Roundtrip_und_Standard_wird_nicht_gespeichert()
    {
        Assert.Null(new BlockProps().ToJson());
        var p = new BlockProps(1.6, true, false, true, -2, 2, null, 10);
        var back = BlockProps.Parse(p.ToJson());
        Assert.Equal(p, back);
        Assert.Equal(new BlockProps(), BlockProps.Parse(null));
        Assert.Equal(new BlockProps(), BlockProps.Parse("{kaputt"));
        Assert.Equal(1.0, BlockProps.Parse("{\"scale\":99}").Scale);          // unsinnige Groesse -> normal
    }

    [Fact]
    public void Props_werden_gespeichert_und_in_der_Vorlesung_geliefert()
    {
        using var s = new NotebookStore(TempDb());
        var page = s.FirstPage(s.CreateNotebook("x"));
        var f = s.SyncFormulas(page, G(new long[] { 1 }), "text")[0];
        s.EditFormula(f.Id, "Wichtig");
        s.SetProps(f.Id, new BlockProps(1.6, true, true, true));
        Assert.Equal(new BlockProps(1.6, true, true, true), s.GetFormula(f.Id)!.Props);
        var groups = new List<StrokeGroup> { new(new long[] { 1 }, Box(10, 100), 0) };
        var block = JsonNode.Parse(LectureBuilder.Build(groups, s.Formulas(page), 900, 1))!["rows"]![0]!["blocks"]![0]!;
        Assert.Equal(1.6, block["props"]!["scale"]!.GetValue<double>());
        Assert.True(block["props"]!["bold"]!.GetValue<bool>());
        Assert.True(block["props"]!["underline"]!.GetValue<bool>());
    }

    [Fact]
    public void Text_mit_unterschiedlichem_Format_wird_nicht_verbunden()
    {
        using var s = new NotebookStore(TempDb());
        var page = s.FirstPage(s.CreateNotebook("x"));
        var r = s.SyncFormulas(page, G(new long[] { 1 }, new long[] { 2 }), "text");
        s.EditFormula(r[0].Id, "Normal");
        s.EditFormula(r[1].Id, "Fett");
        s.SetProps(r[1].Id, new BlockProps(Bold: true));
        var groups = new List<StrokeGroup> { new(new long[] { 1 }, Box(10, 100), 0), new(new long[] { 2 }, Box(120, 200), 0) };
        var blocks = JsonNode.Parse(LectureBuilder.Build(groups, s.Formulas(page), 900, 1))!["rows"]![0]!["blocks"]!.AsArray();
        Assert.Equal(2, blocks.Count);
    }

    [Fact]
    public void Graph_Typ_behaelt_LaTeX_beim_Wechsel_von_Formel_Text_leert()
    {
        using var s = new NotebookStore(TempDb());
        var page = s.FirstPage(s.CreateNotebook("x"));
        var f = s.SyncFormulas(page, G(new long[] { 1 }), "formula")[0];
        s.EditFormula(f.Id, "f(x)=x^2");
        s.SetKind(f.Id, "graph");
        var g = s.GetFormula(f.Id)!;
        Assert.Equal("graph", g.Kind);
        Assert.Equal("f(x)=x^2", g.EditedLatex);                  // Funktion bleibt erhalten
        s.SetKind(f.Id, "text");
        Assert.Null(s.GetFormula(f.Id)!.EditedLatex);              // LaTeX ist als Text sinnlos -> leeren
        Assert.Equal("graph", s.SyncFormulas(page, G(new long[] { 5 }), "graph")[0].Kind);
    }
}
