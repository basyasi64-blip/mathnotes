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
