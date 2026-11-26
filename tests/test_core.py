import shutil
import sqlite3
import pytest
from mathnotes.storage import Store
from mathnotes.models import Stroke, Point, Candidate, RecognitionResult
from mathnotes import recognition, export


def _stroke():
    return Stroke((Point(0, 0, 0.3, 1), Point(5, 7, 0.9, 20)), "#ff0000", 3.5)


@pytest.fixture
def store(tmp_path):
    s = Store(tmp_path / "t.db")
    yield s
    s.close()


def test_roundtrip_nach_neustart(tmp_path):
    p = tmp_path / "t.db"
    s = Store(p); nb = s.create_notebook("Analysis"); page = s.pages(nb)[0]
    ids = s.add_strokes(page, [_stroke()])
    res = RecognitionResult("fake-test", "0", (Candidate(r"\frac{a}{b}", 0.8), Candidate(r"\frac{a}{6}", 0.1)))
    fid = s.add_formula(page, ids, res); s.edit_formula(fid, r"\frac{a}{b}+1"); s.close()
    s2 = Store(p)
    (sid, st), = s2.strokes(page)
    assert st == _stroke()                        # Originalstrich bit-genau zurueck
    f, = s2.formulas(page)
    assert f.recognized_latex == r"\frac{a}{b}"   # Erkenner-Ergebnis bleibt erhalten
    assert f.current_latex == r"\frac{a}{b}+1"   # Korrektur wiedergefunden
    assert [c.latex for c in f.alternatives] == [r"\frac{a}{6}"]
    s2.close()


def test_striche_unveraenderlich(store):
    page = store.pages(store.create_notebook("x"))[0]
    sid, = store.add_strokes(page, [_stroke()])
    with pytest.raises(sqlite3.DatabaseError):
        store.db.execute("UPDATE strokes SET data='{}' WHERE id=?", (sid,))
    with pytest.raises(sqlite3.DatabaseError):
        store.db.execute("DELETE FROM strokes WHERE id=?", (sid,))


def test_versionsverlauf(store):
    page = store.pages(store.create_notebook("x"))[0]
    res = RecognitionResult("e", "1", (Candidate("a", 0.9),))
    fid = store.add_formula(page, [], res)
    store.edit_formula(fid, "b"); store.edit_formula(fid, "c")
    assert [l for l, _ in store.formula_history(fid)] == ["a", "b", "c"]
    with pytest.raises(KeyError):
        store.edit_formula(9999, "z")


def test_nullrecognizer_taeuscht_nichts_vor():
    r = recognition.get("null").recognize([_stroke()])
    assert not r.recognized and r.candidates == ()


def test_backup_und_integritaet(store, tmp_path):
    store.create_notebook("B")
    assert store.integrity_ok()
    store.backup(tmp_path / "b.db")
    b = Store(tmp_path / "b.db")
    assert b.notebooks() == [(1, "B")] and b.integrity_ok()
    b.close()


def test_latex_escaping():
    assert export.escape_text("a_b & 50%") == r"a\_b \& 50\%"


needs_tex = pytest.mark.skipif(shutil.which('pdflatex') is None, reason='pdflatex nicht installiert')


@needs_tex
def test_export_pdf_echt(store, tmp_path):
    nb = store.create_notebook("Titel_mit_Unterstrich"); page = store.pages(nb)[0]
    fid = store.add_formula(page, [], RecognitionResult("e", "1", (Candidate(r"\int_0^1 x^2\,dx=\frac13", 0.9),)))
    store.add_formula(page, [], RecognitionResult("null", "1", ()))   # nicht erkannt -> Hinweis im Dokument
    tex = export.build_tex(store, nb)
    assert r"\int_0^1" in tex and "nicht erkannt" in tex
    ok, log = export.compile_pdf(tex, tmp_path / "o.pdf")
    assert ok, log
    assert (tmp_path / "o.pdf").read_bytes()[:4] == b"%PDF"


@needs_tex
def test_ungueltiges_latex_meldet_fehler(store, tmp_path):
    nb = store.create_notebook("T"); page = store.pages(nb)[0]
    store.add_formula(page, [], RecognitionResult("e", "1", (Candidate(r"\frac{", 0.5),)))
    ok, _ = export.compile_pdf(export.build_tex(store, nb), tmp_path / "o.pdf")
    assert not ok


def test_radieren_blendet_aus_und_bewahrt_original(store):
    page = store.pages(store.create_notebook("x"))[0]
    a, b = store.add_strokes(page, [_stroke(), _stroke()])
    g = store.erase_strokes([a])
    assert g > 0
    assert [i for i, _ in store.strokes(page)] == [b]                       # a ausgeblendet
    assert [i for i, _ in store.strokes(page, include_erased=True)] == [a, b]  # Original noch da
    # Originalzeile ist weiterhin unveraenderlich vorhanden
    assert store.db.execute("SELECT COUNT(*) FROM strokes").fetchone()[0] == 2


def test_wiederherstellen_und_gruppen(store):
    page = store.pages(store.create_notebook("x"))[0]
    ids = store.add_strokes(page, [_stroke(), _stroke(), _stroke()])
    g1 = store.erase_strokes(ids[:2])
    g2 = store.erase_strokes([ids[2]])
    assert g1 != g2 and store.strokes(page) == []
    store.restore_group(g1)
    assert [i for i, _ in store.strokes(page)] == ids[:2]
    assert store.erase_strokes([]) == 0


def test_erkennung_nutzt_nur_sichtbare_striche_schema_bleibt_kompatibel(tmp_path):
    # Datenbank im alten Format (ohne stroke_erasures) wird beim Oeffnen migriert
    import sqlite3
    p = tmp_path / "alt.db"
    s = Store(p); nb = s.create_notebook("x"); s.db.execute("DROP TABLE stroke_erasures"); s.db.commit(); s.close()
    s2 = Store(p)
    page = s2.pages(nb)[0]
    sid, = s2.add_strokes(page, [_stroke()])
    s2.erase_strokes([sid])
    assert s2.strokes(page) == []
    s2.close()


def test_sync_formulas_anlegen_und_wiedererkennen(store):
    page = store.pages(store.create_notebook("x"))[0]
    r1 = store.sync_formulas(page, [[1, 2], [3]])
    assert [r["needs_recognition"] for r in r1] == [True, True]
    f1, f2 = r1[0]["formula_id"], r1[1]["formula_id"]
    assert f1 != f2
    res = RecognitionResult("e", "1", (Candidate("a", None), Candidate("b", None)))
    store.record_recognition(f1, [1, 2], res)
    r2 = store.sync_formulas(page, [[1, 2], [3]])
    assert [r["needs_recognition"] for r in r2] == [False, True]
    assert [r["formula_id"] for r in r2] == [f1, f2]


def test_sync_neue_striche_behalten_formel_und_korrektur(store):
    page = store.pages(store.create_notebook("x"))[0]
    (a,) = store.sync_formulas(page, [[1, 2]])
    fid = a["formula_id"]
    store.record_recognition(fid, [1, 2], RecognitionResult("e", "1", (Candidate("alt", None),)))
    store.edit_formula(fid, "meine Korrektur")
    (b,) = store.sync_formulas(page, [[1, 2, 3]])           # Strich 3 kommt hinzu
    assert b["formula_id"] == fid and b["needs_recognition"] is True
    store.record_recognition(fid, [1, 2, 3], RecognitionResult("e", "1", (Candidate("neu", None),)))
    f, = [x for x in store.formulas(page) if x.id == fid]
    assert f.recognized_latex == "neu" and f.edited_latex == "meine Korrektur"   # Korrektur bleibt


def test_sync_verschwundene_gruppe_wird_nicht_geliefert_aber_nicht_geloescht(store):
    page = store.pages(store.create_notebook("x"))[0]
    r = store.sync_formulas(page, [[1], [2]])
    store.edit_formula(r[1]["formula_id"], "wichtig")
    out = store.sync_formulas(page, [[1]])                  # Strich 2 wurde radiert
    assert [o["formula_id"] for o in out] == [r[0]["formula_id"]]
    assert any(f.edited_latex == "wichtig" for f in store.formulas(page))   # Daten bleiben erhalten


def test_sync_teilung_behaelt_groesseren_teil(store):
    page = store.pages(store.create_notebook("x"))[0]
    (a,) = store.sync_formulas(page, [[1, 2, 3]])
    out = store.sync_formulas(page, [[1, 2], [3]])
    assert out[0]["formula_id"] == a["formula_id"] and out[1]["formula_id"] != a["formula_id"]


@needs_tex
def test_export_lesereihenfolge_und_nur_sichtbare_formeln(store, tmp_path):
    from mathnotes.segmentation import group_strokes
    nb = store.create_notebook("Mehrere"); page = store.pages(nb)[0]

    def box(x, y):
        return Stroke((Point(x, y), Point(x + 40, y + 40)))

    right, left, below, gone = store.add_strokes(page, [box(400, 0), box(0, 0), box(0, 120), box(0, 300)])
    store.erase_strokes([gone])                                   # radiert -> darf nicht exportiert werden
    groups = [list(g.ids) for g in group_strokes(store.strokes(page))]
    assert groups == [[left], [right], [below]]                   # links, rechts, darunter
    synced = store.sync_formulas(page, groups + [[gone]])         # Formel zu radiertem Strich bleibt in der DB
    for s_, text in zip(synced, ["LINKS", "RECHTS", "UNTEN", "RADIERT"]):
        store.record_recognition(s_["formula_id"], s_["stroke_ids"],
                                 RecognitionResult("e", "1", (Candidate(text, None),)))
    tex = export.build_tex(store, nb)
    assert tex.index("LINKS") < tex.index("RECHTS") < tex.index("UNTEN")
    assert "RADIERT" not in tex
    ok, log = export.compile_pdf(tex, tmp_path / "m.pdf")
    assert ok, log
