"""SQLite-Persistenz. Originalstriche sind per Trigger unveraenderlich."""
from __future__ import annotations
import json
import sqlite3
from pathlib import Path
from typing import Sequence
from .models import Stroke, Point, Candidate, RecognitionResult, Formula

SCHEMA = """
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
"""
SCHEMA_VERSION = "1"


def _enc(s: Stroke) -> str:
    return json.dumps({"c": s.color, "w": s.width,
                       "p": [[p.x, p.y, p.pressure, p.t_ms] for p in s.points]})


def _dec(text: str) -> Stroke:
    d = json.loads(text)
    return Stroke(tuple(Point(*p) for p in d["p"]), d["c"], d["w"])


class Store:
    def __init__(self, path: str | Path):
        self.path = str(path)
        self.db = sqlite3.connect(self.path)
        self.db.execute("PRAGMA journal_mode=WAL")
        self.db.execute("PRAGMA foreign_keys=ON")
        self.db.executescript(SCHEMA)
        self._migrate()
        self.db.execute("INSERT OR IGNORE INTO meta VALUES('schema_version', ?)", (SCHEMA_VERSION,))
        self.db.commit()

    def _migrate(self) -> None:
        cols = {r[1] for r in self.db.execute("PRAGMA table_info(formulas)")}
        if "recognized_ids" not in cols:   # Striche, mit denen zuletzt erkannt wurde (JSON)
            self.db.execute("ALTER TABLE formulas ADD COLUMN recognized_ids TEXT")
        if "kind" not in cols:             # 'formula' oder 'text' (bei 'text' enthalten die LaTeX-Spalten normalen Text)
            self.db.execute("ALTER TABLE formulas ADD COLUMN kind TEXT NOT NULL DEFAULT 'formula'")
        if "align" not in cols:            # NULL = automatisch aus der Schreibposition, sonst left/center/right
            self.db.execute("ALTER TABLE formulas ADD COLUMN align TEXT")

    def close(self) -> None:
        self.db.close()

    # --- Notizbuch / Seiten / Striche
    def create_notebook(self, title: str) -> int:
        with self.db:
            nb = self.db.execute("INSERT INTO notebooks(title) VALUES(?)", (title,)).lastrowid
            self.db.execute("INSERT INTO pages(notebook_id, position) VALUES(?,0)", (nb,))
        return nb

    def notebooks(self) -> list[tuple[int, str]]:
        return list(self.db.execute("SELECT id, title FROM notebooks ORDER BY id"))

    def pages(self, notebook_id: int) -> list[int]:
        return [r[0] for r in self.db.execute(
            "SELECT id FROM pages WHERE notebook_id=? ORDER BY position", (notebook_id,))]

    def add_strokes(self, page_id: int, strokes: Sequence[Stroke]) -> list[int]:
        with self.db:
            return [self.db.execute("INSERT INTO strokes(page_id,data) VALUES(?,?)",
                                    (page_id, _enc(s))).lastrowid for s in strokes]

    def strokes(self, page_id: int, include_erased: bool = False) -> list[tuple[int, Stroke]]:
        """Standard: nur sichtbare Striche. Geloeschte bleiben als Original erhalten (include_erased=True)."""
        sql = "SELECT id, data FROM strokes WHERE page_id=?"
        if not include_erased:
            sql += " AND id NOT IN (SELECT stroke_id FROM stroke_erasures)"
        return [(i, _dec(d)) for i, d in self.db.execute(sql + " ORDER BY id", (page_id,))]

    def erase_strokes(self, stroke_ids: Sequence[int]) -> int:
        """Blendet Striche aus (Original bleibt). Gibt die Gruppen-Id zurueck (0 = nichts zu tun)."""
        if not stroke_ids:
            return 0
        with self.db:
            gid = self.db.execute("SELECT COALESCE(MAX(group_id),0)+1 FROM stroke_erasures").fetchone()[0]
            for sid in stroke_ids:
                self.db.execute("INSERT OR IGNORE INTO stroke_erasures(stroke_id, group_id) VALUES(?,?)", (sid, gid))
        return gid

    def restore_group(self, group_id: int) -> None:
        with self.db:
            self.db.execute("DELETE FROM stroke_erasures WHERE group_id=?", (group_id,))

    # --- Formeln
    def add_formula(self, page_id: int, stroke_ids: list[int], result: RecognitionResult) -> int:
        best = result.candidates[0] if result.recognized else None
        alts = [{"latex": c.latex, "confidence": c.confidence} for c in result.candidates[1:]]
        with self.db:
            fid = self.db.execute(
                "INSERT INTO formulas(page_id,stroke_ids,recognized_latex,confidence,alternatives,engine)"
                " VALUES(?,?,?,?,?,?)",
                (page_id, json.dumps(stroke_ids), best.latex if best else None,
                 best.confidence if best else None, json.dumps(alts),
                 f"{result.engine}@{result.engine_version}")).lastrowid
            if best:
                self.db.execute("INSERT INTO formula_versions(formula_id,latex,source) VALUES(?,?,?)",
                                (fid, best.latex, f"recognizer:{result.engine}"))
        return fid

    def sync_formulas(self, page_id: int, groups: Sequence[Sequence[int]]) -> list[dict]:
        """Ordnet Stroh-Gruppen bestehenden Formeln zu (groesste Ueberlappung) oder legt neue an.
        Eine Formel behaelt ihre Id und ihre Nutzerkorrektur, wenn Striche hinzukommen.
        Formeln ohne Gruppe bleiben unveraendert in der Datenbank, werden aber nicht zurueckgegeben."""
        groups = [sorted(set(g)) for g in groups]
        existing = {}
        for fid, sids, rids in self.db.execute(
                "SELECT id, stroke_ids, recognized_ids FROM formulas WHERE page_id=? ORDER BY id", (page_id,)):
            existing[fid] = (set(json.loads(sids)), set(json.loads(rids)) if rids else None)
        pairs = []
        for gi, g in enumerate(groups):
            gs = set(g)
            for fid, (fs, _) in existing.items():
                ov = len(gs & fs)
                if ov:
                    pairs.append((-ov, fid, gi))
        pairs.sort()
        assigned_g: dict[int, int] = {}
        used_f: set[int] = set()
        for _, fid, gi in pairs:
            if gi not in assigned_g and fid not in used_f:
                assigned_g[gi] = fid
                used_f.add(fid)
        out = []
        with self.db:
            for gi, g in enumerate(groups):
                fid = assigned_g.get(gi)
                if fid is None:
                    fid = self.db.execute("INSERT INTO formulas(page_id,stroke_ids) VALUES(?,?)",
                                          (page_id, json.dumps(g))).lastrowid
                    rec = None
                else:
                    if existing[fid][0] != set(g):
                        self.db.execute("UPDATE formulas SET stroke_ids=? WHERE id=?", (json.dumps(g), fid))
                    rec = existing[fid][1]
                out.append({"formula_id": fid, "stroke_ids": g,
                            "needs_recognition": rec is None or rec != set(g)})
        return out

    def record_recognition(self, formula_id: int, stroke_ids: Sequence[int], result: RecognitionResult) -> None:
        """Speichert ein Erkennungsergebnis. Eine vorhandene Nutzerkorrektur bleibt unveraendert."""
        if not result.recognized:
            raise ValueError("nichts erkannt")
        best = result.candidates[0]
        alts = [{"latex": c.latex, "confidence": c.confidence} for c in result.candidates[1:]]
        with self.db:
            cur = self.db.execute(
                "UPDATE formulas SET recognized_latex=?, confidence=?, alternatives=?, engine=?, recognized_ids=? WHERE id=?",
                (best.latex, best.confidence, json.dumps(alts), f"{result.engine}@{result.engine_version}",
                 json.dumps(sorted(set(stroke_ids))), formula_id))
            if cur.rowcount != 1:
                raise KeyError(formula_id)
            self.db.execute("INSERT INTO formula_versions(formula_id,latex,source) VALUES(?,?,?)",
                            (formula_id, best.latex, f"recognizer:{result.engine}"))

    def edit_formula(self, formula_id: int, latex: str) -> None:
        """Nutzerkorrektur: Verlauf bleibt erhalten, Erkenner-Ergebnis wird nicht ueberschrieben."""
        with self.db:
            cur = self.db.execute("UPDATE formulas SET edited_latex=? WHERE id=?", (latex, formula_id))
            if cur.rowcount != 1:
                raise KeyError(formula_id)
            self.db.execute("INSERT INTO formula_versions(formula_id,latex,source) VALUES(?,?,'user')",
                            (formula_id, latex))

    def formula_history(self, formula_id: int) -> list[tuple[str | None, str]]:
        return list(self.db.execute(
            "SELECT latex, source FROM formula_versions WHERE formula_id=? ORDER BY id", (formula_id,)))

    def formulas(self, page_id: int) -> list[Formula]:
        out = []
        for r in self.db.execute(
            "SELECT id,page_id,stroke_ids,recognized_latex,edited_latex,confidence,alternatives,engine"
            " FROM formulas WHERE page_id=? ORDER BY id", (page_id,)):
            out.append(Formula(r[0], r[1], json.loads(r[2]), r[3], r[4], r[5],
                               [Candidate(a["latex"], a["confidence"]) for a in json.loads(r[6])], r[7]))
        return out

    # --- Integritaet & Backup
    def integrity_ok(self) -> bool:
        return self.db.execute("PRAGMA integrity_check").fetchone()[0] == "ok"

    def backup(self, target: str | Path) -> None:
        dst = sqlite3.connect(str(target))
        try:
            self.db.backup(dst)
        finally:
            dst.close()
