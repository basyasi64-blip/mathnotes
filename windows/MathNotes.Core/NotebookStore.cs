using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace MathNotes.Core;

public sealed record PointData(double X, double Y, double Pressure, long TimeMs);
public sealed record StrokeData(string Color, double Width, IReadOnlyList<PointData> Points);

/// <summary>
/// SQLite-Speicher mit demselben Schema wie der Python-Kern (src/mathnotes/storage.py).
/// Originalstriche sind per Trigger unveraenderlich.
/// </summary>
public sealed record FormulaRecord(
    long Id, IReadOnlyList<long> StrokeIds, string? RecognizedLatex, string? EditedLatex,
    IReadOnlyList<long>? RecognizedIds, IReadOnlyList<string> Alternatives,
    string Kind = "formula", string? Align = null)
{
    /// <summary>Bei Kind="text" enthalten Recognized/Edited normalen Text statt LaTeX.</summary>
    /// <summary>Vom Nutzer bestaetigter Text hat Vorrang vor dem Erkenner-Ergebnis.</summary>
    public string? CurrentLatex => EditedLatex ?? RecognizedLatex;

    public bool NeedsRecognition =>
        RecognizedIds is null || !new HashSet<long>(RecognizedIds).SetEquals(StrokeIds);
}

public sealed class NotebookStore : IDisposable
{
    private const string Schema = @"
PRAGMA foreign_keys = ON;
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS notebooks(
  id INTEGER PRIMARY KEY, title TEXT NOT NULL, created_utc TEXT NOT NULL DEFAULT (datetime('now')));
CREATE TABLE IF NOT EXISTS pages(
  id INTEGER PRIMARY KEY, notebook_id INTEGER NOT NULL REFERENCES notebooks(id),
  position INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS strokes(
  id INTEGER PRIMARY KEY, page_id INTEGER NOT NULL REFERENCES pages(id),
  data TEXT NOT NULL, created_utc TEXT NOT NULL DEFAULT (datetime('now')));
CREATE TABLE IF NOT EXISTS stroke_erasures(
  stroke_id INTEGER PRIMARY KEY REFERENCES strokes(id),
  group_id INTEGER NOT NULL, erased_utc TEXT NOT NULL DEFAULT (datetime('now')));
CREATE TABLE IF NOT EXISTS formulas(
  id INTEGER PRIMARY KEY, page_id INTEGER NOT NULL REFERENCES pages(id),
  stroke_ids TEXT NOT NULL, recognized_latex TEXT, edited_latex TEXT,
  confidence REAL, alternatives TEXT NOT NULL DEFAULT '[]', engine TEXT);
CREATE TABLE IF NOT EXISTS formula_versions(
  id INTEGER PRIMARY KEY, formula_id INTEGER NOT NULL REFERENCES formulas(id),
  latex TEXT, source TEXT NOT NULL, created_utc TEXT NOT NULL DEFAULT (datetime('now')));
CREATE TRIGGER IF NOT EXISTS strokes_no_update BEFORE UPDATE ON strokes
  BEGIN SELECT RAISE(ABORT, 'Originalstriche sind unveraenderlich'); END;
CREATE TRIGGER IF NOT EXISTS strokes_no_delete BEFORE DELETE ON strokes
  BEGIN SELECT RAISE(ABORT, 'Originalstriche sind unveraenderlich'); END;
";

    private readonly SqliteConnection _db;

    public NotebookStore(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL");
        Exec(Schema);
        Migrate();
        Exec("INSERT OR IGNORE INTO meta VALUES('schema_version','1')");
    }

    private void Migrate()
    {
        var cols = new HashSet<string>();
        using (var c = _db.CreateCommand())
        {
            c.CommandText = "PRAGMA table_info(formulas)";
            using var r = c.ExecuteReader();
            while (r.Read()) cols.Add(r.GetString(1));
        }
        if (!cols.Contains("recognized_ids")) Exec("ALTER TABLE formulas ADD COLUMN recognized_ids TEXT");
        if (!cols.Contains("kind")) Exec("ALTER TABLE formulas ADD COLUMN kind TEXT NOT NULL DEFAULT 'formula'");
        if (!cols.Contains("align")) Exec("ALTER TABLE formulas ADD COLUMN align TEXT");
    }

    private long Exec(string sql, params (string, object?)[] args)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        c.ExecuteNonQuery();
        return 0;
    }

    private T? Scalar<T>(string sql, params (string, object?)[] args)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v ?? DBNull.Value);
        var r = c.ExecuteScalar();
        if (r is null or DBNull) return default;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(r, target);
    }

    public long CreateNotebook(string title)
    {
        using var tx = _db.BeginTransaction();
        Exec("INSERT INTO notebooks(title) VALUES($t)", ("$t", title));
        var nb = Scalar<long>("SELECT last_insert_rowid()");
        Exec("INSERT INTO pages(notebook_id, position) VALUES($n,0)", ("$n", nb));
        tx.Commit();
        return nb;
    }

    public long? FirstNotebook() => Scalar<long?>("SELECT id FROM notebooks ORDER BY id LIMIT 1");

    public long FirstPage(long notebookId) =>
        Scalar<long>("SELECT id FROM pages WHERE notebook_id=$n ORDER BY position LIMIT 1", ("$n", notebookId));

    public long AddStroke(long pageId, StrokeData s)
    {
        var json = StrokeJson.Encode(s);
        Exec("INSERT INTO strokes(page_id,data) VALUES($p,$d)", ("$p", pageId), ("$d", json));
        return Scalar<long>("SELECT last_insert_rowid()");
    }

    /// <summary>Nur sichtbare Striche. Geloeschte bleiben als Original erhalten (includeErased).</summary>
    public List<(long Id, StrokeData Stroke)> Strokes(long pageId, bool includeErased = false)
    {
        var res = new List<(long, StrokeData)>();
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT id,data FROM strokes WHERE page_id=$p" +
            (includeErased ? "" : " AND id NOT IN (SELECT stroke_id FROM stroke_erasures)") +
            " ORDER BY id";
        c.Parameters.AddWithValue("$p", pageId);
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            res.Add((r.GetInt64(0), StrokeJson.Decode(r.GetString(1))));
        }
        return res;
    }

    /// <summary>Blendet Striche aus (Original bleibt). Gibt die Gruppen-Id zurueck, 0 wenn nichts zu tun war.</summary>
    public long EraseStrokes(IReadOnlyList<long> strokeIds)
    {
        if (strokeIds.Count == 0) return 0;
        using var tx = _db.BeginTransaction();
        var gid = Scalar<long>("SELECT COALESCE(MAX(group_id),0)+1 FROM stroke_erasures");
        foreach (var id in strokeIds)
            Exec("INSERT OR IGNORE INTO stroke_erasures(stroke_id, group_id) VALUES($s,$g)", ("$s", id), ("$g", gid));
        tx.Commit();
        return gid;
    }

    public void RestoreGroup(long groupId) =>
        Exec("DELETE FROM stroke_erasures WHERE group_id=$g", ("$g", groupId));

    private static List<long> ParseIds(string json) =>
        JsonNode.Parse(json)!.AsArray().Select(x => x!.GetValue<long>()).ToList();

    private static FormulaRecord ReadFormula(SqliteDataReader r) => new(
        r.GetInt64(0),
        ParseIds(r.GetString(1)),
        r.IsDBNull(2) ? null : r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) ? null : ParseIds(r.GetString(4)),
        r.IsDBNull(5)
            ? new List<string>()
            : JsonNode.Parse(r.GetString(5))!.AsArray().Select(a => a!["latex"]!.GetValue<string>()).ToList(),
        r.GetString(6),
        r.IsDBNull(7) ? null : r.GetString(7));

    private const string FormulaColumns =
        "SELECT id, stroke_ids, recognized_latex, edited_latex, recognized_ids, alternatives, kind, align FROM formulas";

    public List<FormulaRecord> Formulas(long pageId)
    {
        var res = new List<FormulaRecord>();
        using var c = _db.CreateCommand();
        c.CommandText = FormulaColumns + " WHERE page_id=$p ORDER BY id";
        c.Parameters.AddWithValue("$p", pageId);
        using var r = c.ExecuteReader();
        while (r.Read()) res.Add(ReadFormula(r));
        return res;
    }

    public FormulaRecord? GetFormula(long formulaId)
    {
        using var c = _db.CreateCommand();
        c.CommandText = FormulaColumns + " WHERE id=$f";
        c.Parameters.AddWithValue("$f", formulaId);
        using var r = c.ExecuteReader();
        return r.Read() ? ReadFormula(r) : null;
    }

    /// <summary>
    /// Ordnet Strich-Gruppen bestehenden Formeln zu (groesste Ueberlappung) oder legt neue an.
    /// Eine Formel behaelt Id und Nutzerkorrektur, wenn Striche hinzukommen. Formeln ohne Gruppe bleiben
    /// unveraendert in der Datenbank, werden aber nicht zurueckgegeben. Ergebnis in Reihenfolge der Gruppen.
    /// </summary>
    public List<FormulaRecord> SyncFormulas(long pageId, IReadOnlyList<IReadOnlyList<long>> groups, string defaultKind = "formula")
    {
        var gs = groups.Select(g => g.Distinct().OrderBy(x => x).ToList()).ToList();
        var existing = Formulas(pageId);

        var pairs = new List<(int Overlap, long Fid, int Gi)>();
        for (var gi = 0; gi < gs.Count; gi++)
        {
            var set = new HashSet<long>(gs[gi]);
            foreach (var f in existing)
            {
                var ov = f.StrokeIds.Count(set.Contains);
                if (ov > 0) pairs.Add((ov, f.Id, gi));
            }
        }
        var assignedG = new Dictionary<int, long>();
        var usedF = new HashSet<long>();
        foreach (var pr in pairs.OrderByDescending(x => x.Overlap).ThenBy(x => x.Fid).ThenBy(x => x.Gi))
        {
            if (assignedG.ContainsKey(pr.Gi) || usedF.Contains(pr.Fid)) continue;
            assignedG[pr.Gi] = pr.Fid;
            usedF.Add(pr.Fid);
        }

        var ids = new List<long>();
        using (var tx = _db.BeginTransaction())
        {
            for (var gi = 0; gi < gs.Count; gi++)
            {
                var json = "[" + string.Join(",", gs[gi]) + "]";
                if (assignedG.TryGetValue(gi, out var fid))
                {
                    var old = existing.First(f => f.Id == fid);
                    if (!new HashSet<long>(old.StrokeIds).SetEquals(gs[gi]))
                        Exec("UPDATE formulas SET stroke_ids=$s WHERE id=$f", ("$s", json), ("$f", fid));
                }
                else
                {
                    Exec("INSERT INTO formulas(page_id,stroke_ids,kind) VALUES($p,$s,$k)", ("$p", pageId), ("$s", json), ("$k", defaultKind));
                    fid = Scalar<long>("SELECT last_insert_rowid()");
                }
                ids.Add(fid);
            }
            tx.Commit();
        }
        return ids.Select(id => GetFormula(id)!).ToList();
    }

    /// <summary>Speichert ein Erkennungsergebnis fuer eine Formel. Eine Nutzerkorrektur bleibt unveraendert.</summary>
    public void SaveRecognitionFor(long formulaId, IReadOnlyList<long> strokeIds, RecognitionOutcome r)
    {
        if (!r.Recognized) throw new InvalidOperationException("Nichts erkannt");
        if (Scalar<long?>("SELECT id FROM formulas WHERE id=$f", ("$f", formulaId)) is null)
            throw new KeyNotFoundException($"Formel {formulaId}");
        var best = r.Candidates[0];
        var alts = new JsonArray();
        foreach (var c in r.Candidates.Skip(1))
            alts.Add(new JsonObject { ["latex"] = c.Latex, ["confidence"] = c.Confidence });
        var ids = "[" + string.Join(",", strokeIds.Distinct().OrderBy(x => x)) + "]";
        using var tx = _db.BeginTransaction();
        Exec("UPDATE formulas SET recognized_latex=$l, confidence=$c, alternatives=$a, engine=$e, recognized_ids=$ri WHERE id=$f",
             ("$l", best.Latex), ("$c", best.Confidence), ("$a", alts.ToJsonString()),
             ("$e", $"{r.Engine}@{r.EngineVersion}"), ("$ri", ids), ("$f", formulaId));
        Exec("INSERT INTO formula_versions(formula_id,latex,source) VALUES($f,$l,$src)",
             ("$f", formulaId), ("$l", best.Latex), ("$src", "recognizer:" + r.Engine));
        tx.Commit();
    }

    /// <summary>
    /// Wechselt den Typ eines Blocks (formula/text). Inhalt und Erkennung gehoeren zum alten Typ und werden
    /// geleert, damit neu erkannt wird; der bisherige Inhalt bleibt im Versionsverlauf erhalten.
    /// </summary>
    public void SetKind(long formulaId, string kind)
    {
        if (kind is not ("formula" or "text")) throw new ArgumentException("Typ muss formula oder text sein");
        var cur = GetFormula(formulaId) ?? throw new KeyNotFoundException($"Formel {formulaId}");
        if (cur.Kind == kind) return;
        using var tx = _db.BeginTransaction();
        Exec("UPDATE formulas SET kind=$k, recognized_latex=NULL, edited_latex=NULL, confidence=NULL, alternatives='[]', engine=NULL, recognized_ids=NULL WHERE id=$f",
             ("$k", kind), ("$f", formulaId));
        Exec("INSERT INTO formula_versions(formula_id,latex,source) VALUES($f,NULL,$s)", ("$f", formulaId), ("$s", "kind:" + kind));
        tx.Commit();
    }

    /// <summary>align: null = automatisch (aus der Schreibposition), sonst left/center/right.</summary>
    public void SetAlign(long formulaId, string? align)
    {
        if (align is not (null or "left" or "center" or "right")) throw new ArgumentException("Ausrichtung ungueltig");
        if (Scalar<long?>("SELECT id FROM formulas WHERE id=$f", ("$f", formulaId)) is null)
            throw new KeyNotFoundException($"Formel {formulaId}");
        Exec("UPDATE formulas SET align=$a WHERE id=$f", ("$a", align), ("$f", formulaId));
    }

    /// <summary>Nutzerkorrektur einer bestimmten Formel; Verlauf bleibt erhalten.</summary>
    public void EditFormula(long formulaId, string latex)
    {
        if (Scalar<long?>("SELECT id FROM formulas WHERE id=$f", ("$f", formulaId)) is null)
            throw new KeyNotFoundException($"Formel {formulaId}");
        using var tx = _db.BeginTransaction();
        Exec("UPDATE formulas SET edited_latex=$l WHERE id=$f", ("$l", latex), ("$f", formulaId));
        Exec("INSERT INTO formula_versions(formula_id,latex,source) VALUES($f,$l,'user')",
             ("$f", formulaId), ("$l", latex));
        tx.Commit();
    }

    /// <summary>Speichert einen Erkenner-Vorschlag. Eine vorhandene Nutzerkorrektur bleibt unveraendert.</summary>
    public long SaveRecognition(long pageId, IReadOnlyList<long> strokeIds, RecognitionOutcome r)
    {
        if (!r.Recognized) throw new InvalidOperationException("Nichts erkannt");
        var best = r.Candidates[0];
        var alts = new JsonArray();
        foreach (var c in r.Candidates.Skip(1))
            alts.Add(new JsonObject { ["latex"] = c.Latex, ["confidence"] = c.Confidence });
        var ids = "[" + string.Join(",", strokeIds) + "]";
        var engine = $"{r.Engine}@{r.EngineVersion}";
        using var tx = _db.BeginTransaction();
        var fid = Scalar<long?>("SELECT id FROM formulas WHERE page_id=$p ORDER BY id LIMIT 1", ("$p", pageId));
        if (fid is null)
        {
            Exec("INSERT INTO formulas(page_id,stroke_ids,recognized_latex,confidence,alternatives,engine) VALUES($p,$s,$l,$c,$a,$e)",
                 ("$p", pageId), ("$s", ids), ("$l", best.Latex), ("$c", best.Confidence), ("$a", alts.ToJsonString()), ("$e", engine));
            fid = Scalar<long>("SELECT last_insert_rowid()");
        }
        else
        {
            Exec("UPDATE formulas SET stroke_ids=$s, recognized_latex=$l, confidence=$c, alternatives=$a, engine=$e WHERE id=$f",
                 ("$s", ids), ("$l", best.Latex), ("$c", best.Confidence), ("$a", alts.ToJsonString()), ("$e", engine), ("$f", fid));
        }
        Exec("INSERT INTO formula_versions(formula_id,latex,source) VALUES($f,$l,$src)",
             ("$f", fid), ("$l", best.Latex), ("$src", "recognizer:" + r.Engine));
        tx.Commit();
        return fid.Value;
    }

    public string? RecognizedFormula(long pageId) =>
        Scalar<string?>("SELECT recognized_latex FROM formulas WHERE page_id=$p ORDER BY id LIMIT 1", ("$p", pageId));

    /// <summary>Nutzerkorrektur der Formel; erzeugt die Formel beim ersten Speichern, Verlauf bleibt erhalten.</summary>
    public long SaveEditedFormula(long pageId, string latex)
    {
        using var tx = _db.BeginTransaction();
        var fid = Scalar<long?>("SELECT id FROM formulas WHERE page_id=$p ORDER BY id LIMIT 1", ("$p", pageId));
        if (fid is null)
        {
            Exec("INSERT INTO formulas(page_id,stroke_ids,edited_latex) VALUES($p,'[]',$l)",
                 ("$p", pageId), ("$l", latex));
            fid = Scalar<long>("SELECT last_insert_rowid()");
        }
        else
        {
            Exec("UPDATE formulas SET edited_latex=$l WHERE id=$f", ("$l", latex), ("$f", fid));
        }
        Exec("INSERT INTO formula_versions(formula_id,latex,source) VALUES($f,$l,'user')",
             ("$f", fid), ("$l", latex));
        tx.Commit();
        return fid.Value;
    }

    public string? EditedFormula(long pageId) =>
        Scalar<string?>("SELECT edited_latex FROM formulas WHERE page_id=$p ORDER BY id LIMIT 1", ("$p", pageId));

    public int FormulaVersionCount(long pageId) =>
        (int)Scalar<long>("SELECT COUNT(*) FROM formula_versions WHERE formula_id IN (SELECT id FROM formulas WHERE page_id=$p)", ("$p", pageId));

    public bool IntegrityOk() => Scalar<string>("PRAGMA integrity_check") == "ok";

    /// <summary>Fuer Tests: fuehrt rohes SQL aus (um Trigger zu pruefen).</summary>
    public void ExecRaw(string sql) => Exec(sql);

    public void Dispose() => _db.Dispose();
}
