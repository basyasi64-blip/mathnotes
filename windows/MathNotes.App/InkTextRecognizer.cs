using System.Numerics;
using MathNotes.Core;
using Windows.UI.Input.Inking;

namespace MathNotes.App;

/// <summary>
/// Erkennt normalen Text aus den Strichen mit der in Windows eingebauten Handschrifterkennung
/// (lokal, ohne Download). Bevorzugt Deutsch. Fehler werden als Ergebnis gemeldet, nie als Absturz.
/// </summary>
public static class InkTextRecognizer
{
    public static async Task<RecognitionOutcome> RecognizeAsync(IReadOnlyList<StrokeData> strokes)
    {
        try
        {
            if (strokes.Count == 0) return Fail("keine Striche");

            var container = new InkStrokeContainer();
            var builder = new InkStrokeBuilder();
            foreach (var s in strokes)
            {
                var pts = s.Points
                    .Select(p => new InkPoint(new Windows.Foundation.Point(p.X, p.Y), (float)Math.Clamp(p.Pressure, 0.05, 1.0)))
                    .ToList();
                if (pts.Count == 1)   // Punkte (z. B. i-Punkt) brauchen mindestens zwei Stuetzpunkte
                    pts.Add(new InkPoint(new Windows.Foundation.Point(s.Points[0].X + 0.1, s.Points[0].Y + 0.1), pts[0].Pressure));
                container.AddStroke(builder.CreateStrokeFromInkPoints(pts, Matrix3x2.Identity));
            }

            var recognizers = new InkRecognizerContainer();
            var all = recognizers.GetRecognizers();
            if (all.Count == 0) return Fail("Keine Windows-Handschrifterkennung installiert");
            var chosen =
                all.FirstOrDefault(r => r.Name.Contains("Deutsch") && !r.Name.Contains("Schweiz")) ??
                all.FirstOrDefault(r => r.Name.Contains("German") && !r.Name.Contains("Switzerland")) ??
                all.FirstOrDefault(r => r.Name.Contains("Handschrift") || r.Name.Contains("Handwriting")) ??
                all[0];
            recognizers.SetDefaultRecognizer(chosen);

            var results = await recognizers.RecognizeAsync(container, InkRecognitionTarget.All);
            if (results.Count == 0) return Fail("kein Text erkannt");

            // Woerter in Lesereihenfolge: Zeilen von oben nach unten, innerhalb einer Zeile von links nach rechts
            var items = results
                .Select(r => (Rect: r.BoundingRect, Cands: r.GetTextCandidates()))
                .Where(x => x.Cands.Count > 0)
                .OrderBy(x => x.Rect.Y + x.Rect.Height / 2)
                .ToList();
            if (items.Count == 0) return Fail("kein Text erkannt");
            var heights = items.Select(x => x.Rect.Height).OrderBy(h => h).ToList();
            var tol = Math.Max(10.0, 0.6 * heights[heights.Count / 2]);
            var lines = new List<List<(Windows.Foundation.Rect Rect, IReadOnlyList<string> Cands)>>();
            foreach (var it in items)
            {
                var cy = it.Rect.Y + it.Rect.Height / 2;
                if (lines.Count > 0 &&
                    Math.Abs(cy - lines[^1].Average(w => w.Rect.Y + w.Rect.Height / 2)) <= tol)
                    lines[^1].Add(it);
                else
                    lines.Add(new List<(Windows.Foundation.Rect, IReadOnlyList<string>)> { it });
            }

            string Join(Func<IReadOnlyList<string>, string> pick) => string.Join("\n",
                lines.Select(l => string.Join(" ", l.OrderBy(w => w.Rect.X).Select(w => pick(w.Cands)))));

            var best = Join(c => c[0]);
            var alt = Join(c => c.Count > 1 ? c[1] : c[0]);   // Alternative: jeweils zweitbester Kandidat
            var cands = new List<RecognitionCandidate> { new(best, null) };
            if (!string.IsNullOrWhiteSpace(alt) && alt != best) cands.Add(new RecognitionCandidate(alt, null));
            return new RecognitionOutcome("windows-ink", chosen.Name, cands, null);
        }
        catch (Exception ex)
        {
            return Fail($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static RecognitionOutcome Fail(string msg) =>
        new("windows-ink", "?", Array.Empty<RecognitionCandidate>(), msg);
}
