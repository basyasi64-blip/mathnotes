using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace MathNotes.Core;

public sealed record PointData(double X, double Y, double Pressure, long TimeMs);
public sealed record StrokeData(string Color, double Width, IReadOnlyList<PointData> Points);

/// <summary>
/// SQLite-Speicher mit demselben Schema wie der Python-Kern (src/mathnotes/storage.py).
/// Originalstriche sind per Trigger unveraenderlich.
/// </summary>
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
        Exec("INSERT OR IGNORE INTO meta VALUES('schema_version','1')");
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
        return r is null or DBNull ? default : (T)Convert.ChangeType(r, typeof(T));
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
        var pts = new JsonArray();
        foreach (var p in s.Points)
            pts.Add(new JsonArray(p.X, p.Y, p.Pressure, p.TimeMs));
        var json = new JsonObject { ["c"] = s.Color, ["w"] = s.Width, ["p"] = pts }.ToJsonString();
        Exec("INSERT INTO strokes(page_id,data) VALUES($p,$d)", ("$p", pageId), ("$d", json));
        return Scalar<long>("SELECT last_insert_rowid()");
    }

    public List<(long Id, StrokeData Stroke)> Strokes(long pageId)
    {
        var res = new List<(long, StrokeData)>();
        using var c = _db.CreateCommand();
        c.CommandText = "SELECT id,data FROM strokes WHERE page_id=$p ORDER BY id";
        c.Parameters.AddWithValue("$p", pageId);
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            var o = JsonNode.Parse(r.GetString(1))!.AsObject();
            var pts = o["p"]!.AsArray().Select(a =>
            {
                var x = a!.AsArray();
                return new PointData(x[0]!.GetValue<double>(), x[1]!.GetValue<double>(),
                                     x[2]!.GetValue<double>(), x[3]!.GetValue<long>());
            }).ToList();
            res.Add((r.GetInt64(0), new StrokeData(o["c"]!.GetValue<string>(), o["w"]!.GetValue<double>(), pts)));
        }
        return res;
    }

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
