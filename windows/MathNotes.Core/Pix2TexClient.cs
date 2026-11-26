using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace MathNotes.Core;

public sealed record RecognitionCandidate(string Latex, double? Confidence);

public sealed record RecognitionOutcome(
    string Engine, string EngineVersion, IReadOnlyList<RecognitionCandidate> Candidates, string? Error)
{
    public bool Recognized => Error is null && Candidates.Count > 0;
}

/// <summary>
/// Ruft das lokale Python-Skript recognizer/recognize.py (pix2tex) als eigenen Prozess auf.
/// Ein Fehler oder Absturz des Erkenners blockiert die App nie: er wird als Fehler-Ergebnis gemeldet.
/// </summary>
public sealed class Pix2TexClient
{
    private readonly string _python;
    private readonly string _script;

    public Pix2TexClient(string pythonExe, string scriptPath)
    {
        _python = pythonExe;
        _script = scriptPath;
    }

    public static string DefaultPythonPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MathNotes", "recognizer", "venv", "Scripts", "python.exe");

    public bool IsInstalled => File.Exists(_python) && File.Exists(_script);

    public async Task<RecognitionOutcome> RecognizeAsync(
        IReadOnlyList<StrokeData> strokes, TimeSpan timeout, CancellationToken ct = default)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"mn_strokes_{Guid.NewGuid():N}.json");
        try
        {
            var arr = new JsonArray();
            foreach (var s in strokes) arr.Add(StrokeJson.ToNode(s));
            await File.WriteAllTextAsync(tmp, new JsonObject { ["strokes"] = arr }.ToJsonString(), ct);

            var psi = new ProcessStartInfo(_python)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add(_script);
            psi.ArgumentList.Add(tmp);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Prozess konnte nicht gestartet werden");
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { /* bereits beendet */ }
                return Fail("Abgebrochen oder Zeitueberschreitung");
            }

            var stdout = await outTask;
            if (string.IsNullOrWhiteSpace(stdout))
            {
                var err = await errTask;
                return Fail("Erkenner lieferte keine Ausgabe. " + Tail(err, 300));
            }
            return ParseOutput(stdout);
        }
        catch (Exception ex)
        {
            return Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* egal */ }
        }
    }

    /// <summary>Liest die JSON-Zeile des Skripts. Oeffentlich fuer Tests.</summary>
    public static RecognitionOutcome ParseOutput(string output)
    {
        var line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        if (line is null) return Fail("Keine gueltige Ausgabe: " + Tail(output, 200));
        try
        {
            var o = JsonNode.Parse(line)!.AsObject();
            if (o["error"] is JsonNode e) return Fail(e.GetValue<string>());
            var cands = (o["candidates"] as JsonArray ?? new JsonArray())
                .Select(c => new RecognitionCandidate(
                    c!["latex"]!.GetValue<string>(),
                    c["confidence"] is JsonNode cf ? cf.GetValue<double>() : (double?)null))
                .ToList();
            return new RecognitionOutcome(
                o["engine"]?.GetValue<string>() ?? "pix2tex",
                o["engine_version"]?.GetValue<string>() ?? "?",
                cands, null);
        }
        catch (Exception ex)
        {
            return Fail("Ausgabe nicht lesbar: " + ex.Message);
        }
    }

    private static RecognitionOutcome Fail(string msg) =>
        new("pix2tex", "?", Array.Empty<RecognitionCandidate>(), msg);

    private static string Tail(string s, int n) => s.Length <= n ? s : s[^n..];
}
