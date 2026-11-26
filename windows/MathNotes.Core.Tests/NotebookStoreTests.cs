using Microsoft.Data.Sqlite;
using MathNotes.Core;
using Xunit;

namespace MathNotes.Core.Tests;

public class NotebookStoreTests
{
    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"mn_{Guid.NewGuid():N}.db");

    private static StrokeData Sample() => new("#ff0000", 3.5,
        new[] { new PointData(0, 0, 0.3, 1), new PointData(5, 7, 0.9, 20) });

    [Fact]
    public void Roundtrip_NachNeustart()
    {
        var path = TempDb();
        long page;
        using (var s = new NotebookStore(path))
        {
            var nb = s.CreateNotebook("Analysis");
            page = s.FirstPage(nb);
            s.AddStroke(page, Sample());
            s.SaveEditedFormula(page, @"\frac{a}{b}");
        }
        using var s2 = new NotebookStore(path);
        var strokes = s2.Strokes(page);
        Assert.Single(strokes);
        Assert.Equal("#ff0000", strokes[0].Stroke.Color);
        Assert.Equal(3.5, strokes[0].Stroke.Width);
        Assert.Equal(new PointData(5, 7, 0.9, 20), strokes[0].Stroke.Points[1]);
        Assert.Equal(@"\frac{a}{b}", s2.EditedFormula(page));
        Assert.True(s2.IntegrityOk());
    }

    [Fact]
    public void Originalstriche_SindUnveraenderlich()
    {
        using var s = new NotebookStore(TempDb());
        var page = s.FirstPage(s.CreateNotebook("x"));
        var id = s.AddStroke(page, Sample());
        Assert.Throws<SqliteException>(() => s.ExecRaw($"UPDATE strokes SET data='{{}}' WHERE id={id}"));
        Assert.Throws<SqliteException>(() => s.ExecRaw($"DELETE FROM strokes WHERE id={id}"));
    }

    [Fact]
    public void ZweiterStart_FindetBestehendesNotizbuch()
    {
        var path = TempDb();
        long nb;
        using (var s = new NotebookStore(path)) nb = s.CreateNotebook("A");
        using var s2 = new NotebookStore(path);
        Assert.Equal(nb, s2.FirstNotebook());
    }

    [Fact]
    public void Formelkorrektur_FuehrtVerlauf()
    {
        using var s = new NotebookStore(TempDb());
        var page = s.FirstPage(s.CreateNotebook("x"));
        s.SaveEditedFormula(page, "a");
        s.SaveEditedFormula(page, "b");
        Assert.Equal("b", s.EditedFormula(page));
        Assert.Equal(2, s.FormulaVersionCount(page));
    }

    [Fact]
    public void Erkennung_UeberschreibtKeineNutzerkorrektur()
    {
        using var s = new NotebookStore(TempDb());
        var page = s.FirstPage(s.CreateNotebook("x"));
        var id = s.AddStroke(page, Sample());
        s.SaveEditedFormula(page, "meine Korrektur");
        var r = new RecognitionOutcome("pix2tex", "0.1.4",
            new[] { new RecognitionCandidate(@"\frac{1}{2}", null) }, null);
        s.SaveRecognition(page, new[] { id }, r);
        Assert.Equal("meine Korrektur", s.EditedFormula(page));
        Assert.Equal(@"\frac{1}{2}", s.RecognizedFormula(page));
        Assert.Equal(2, s.FormulaVersionCount(page));
    }

    [Fact]
    public void Erkennung_OhneErgebnis_WirdNichtGespeichert()
    {
        using var s = new NotebookStore(TempDb());
        var page = s.FirstPage(s.CreateNotebook("x"));
        var r = new RecognitionOutcome("pix2tex", "?", Array.Empty<RecognitionCandidate>(), "Fehler");
        Assert.Throws<InvalidOperationException>(() => s.SaveRecognition(page, Array.Empty<long>(), r));
    }
}
