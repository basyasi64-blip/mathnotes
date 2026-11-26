using MathNotes.Core;
using Xunit;

namespace MathNotes.Core.Tests;

public class FormulaSyncTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"mn_{Guid.NewGuid():N}.db");

    private static RecognitionOutcome Ok(params string[] latex) => new("t", "1",
        latex.Select(l => new RecognitionCandidate(l, null)).ToList(), null);

    private static IReadOnlyList<IReadOnlyList<long>> G(params long[][] groups) => groups.Select(g => (IReadOnlyList<long>)g).ToList();

    [Fact]
    public void Anlegen_und_Wiedererkennen()
    {
        using var s = new NotebookStore(TempDb());
        var page = s.FirstPage(s.CreateNotebook("x"));
        var r1 = s.SyncFormulas(page, G(new long[] { 1, 2 }, new long[] { 3 }));
        Assert.All(r1, f => Assert.True(f.NeedsRecognition));
        s.SaveRecognitionFor(r1[0].Id, new long[] { 1, 2 }, Ok("a", "b"));
        var r2 = s.SyncFormulas(page, G(new long[] { 1, 2 }, new long[] { 3 }));
        Assert.False(r2[0].NeedsRecognition);
        Assert.True(r2[1].NeedsRecognition);
        Assert.Equal(new[] { r1[0].Id, r1[1].Id }, r2.Select(f => f.Id).ToArray());
        Assert.Equal(new[] { "b" }, r2[0].Alternatives.ToArray());
    }

    [Fact]
    public void NeueStriche_behalten_Formel_und_Korrektur()
    {
        using var s = new NotebookStore(TempDb());
        var page = s.FirstPage(s.CreateNotebook("x"));
        var a = s.SyncFormulas(page, G(new long[] { 1, 2 }))[0];
        s.SaveRecognitionFor(a.Id, new long[] { 1, 2 }, Ok("alt"));
        s.EditFormula(a.Id, "meine Korrektur");
        var b = s.SyncFormulas(page, G(new long[] { 1, 2, 3 }))[0];
        Assert.Equal(a.Id, b.Id);
        Assert.True(b.NeedsRecognition);
        s.SaveRecognitionFor(b.Id, new long[] { 1, 2, 3 }, Ok("neu"));
        var f = s.GetFormula(a.Id)!;
        Assert.Equal("neu", f.RecognizedLatex);
        Assert.Equal("meine Korrektur", f.EditedLatex);
        Assert.Equal("meine Korrektur", f.CurrentLatex);
    }

    [Fact]
    public void VerschwundeneGruppe_wird_nicht_geliefert_aber_nicht_geloescht()
    {
        using var s = new NotebookStore(TempDb());
        var page = s.FirstPage(s.CreateNotebook("x"));
        var r = s.SyncFormulas(page, G(new long[] { 1 }, new long[] { 2 }));
        s.EditFormula(r[1].Id, "wichtig");
        var out1 = s.SyncFormulas(page, G(new long[] { 1 }));
        Assert.Equal(new[] { r[0].Id }, out1.Select(f => f.Id).ToArray());
        Assert.Contains(s.Formulas(page), f => f.EditedLatex == "wichtig");
    }

    [Fact]
    public void Teilung_behaelt_groesseren_Teil()
    {
        using var s = new NotebookStore(TempDb());
        var page = s.FirstPage(s.CreateNotebook("x"));
        var a = s.SyncFormulas(page, G(new long[] { 1, 2, 3 }))[0];
        var o = s.SyncFormulas(page, G(new long[] { 1, 2 }, new long[] { 3 }));
        Assert.Equal(a.Id, o[0].Id);
        Assert.NotEqual(a.Id, o[1].Id);
    }

    [Fact]
    public void AlteDatenbank_ohne_Spalte_wird_migriert()
    {
        var path = TempDb();
        using (var s = new NotebookStore(path)) s.CreateNotebook("x");
        using var s2 = new NotebookStore(path);   // zweites Oeffnen darf die Migration nicht doppelt ausfuehren
        var page = s2.FirstPage(s2.FirstNotebook()!.Value);
        Assert.Single(s2.SyncFormulas(page, G(new long[] { 1 })));
    }

    [Fact]
    public async Task Service_ohne_Installation_gibt_Fehler_statt_Absturz()
    {
        using var svc = new RecognizerService(@"C:\\gibt\\es\\nicht\\python.exe", "x.py");
        Assert.False(svc.IsInstalled);
        var r = await svc.RecognizeAsync(new[] { new StrokeData("#000", 2, new[] { new PointData(0, 0, .5, 0) }) });
        Assert.False(r.Recognized);
        Assert.NotNull(r.Error);
    }
}
