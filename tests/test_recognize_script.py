import importlib.util
import json
import subprocess
import sys
from pathlib import Path

import pytest

SCRIPT = Path(__file__).resolve().parents[1] / "recognizer" / "recognize.py"
PIL = pytest.importorskip("PIL")


def _strokes_file(tmp_path, strokes):
    p = tmp_path / "s.json"
    p.write_text(json.dumps({"strokes": strokes}), encoding="utf-8")
    return p


def _run(*args):
    r = subprocess.run([sys.executable, str(SCRIPT), *map(str, args)], capture_output=True, text=True)
    return r.returncode, r.stdout.strip(), r.stderr


def test_rasterize_erzeugt_bild(tmp_path):
    # ein "L"-foermiger Strich und ein Punkt
    f = _strokes_file(tmp_path, [
        {"c": "#000", "w": 2, "p": [[10, 10, .5, 0], [10, 90, .5, 5], [60, 90, .5, 9]]},
        {"c": "#000", "w": 2, "p": [[80, 50, .5, 20]]},
    ])
    out = tmp_path / "o.png"
    code, stdout, _ = _run(f, "--rasterize-only", out)
    assert code == 0
    info = json.loads(stdout)
    assert out.exists() and info["size"][1] > 50
    from PIL import Image
    im = Image.open(out).convert("L")
    assert im.getpixel((2, 2)) == 255                    # Rand weiss
    assert min(im.getdata()) == 0                        # es wurde wirklich gezeichnet


def test_leere_striche_geben_fehler_statt_absturz(tmp_path):
    f = _strokes_file(tmp_path, [])
    code, stdout, _ = _run(f)
    assert code == 2 and "error" in json.loads(stdout)


def test_fehlendes_modell_meldet_klaren_fehler(tmp_path):
    # In der CI ist pix2tex nicht installiert -> sauberer Fehler, kein Traceback
    if importlib.util.find_spec("pix2tex") is not None:
        pytest.skip("pix2tex ist installiert")
    f = _strokes_file(tmp_path, [{"c": "#000", "w": 2, "p": [[0, 0, .5, 0], [30, 40, .5, 3]]}])
    code, stdout, _ = _run(f)
    assert code == 2
    assert "pix2tex" in json.loads(stdout)["error"] or "fehlt" in json.loads(stdout)["error"]


def test_zwei_darstellungen_unterscheiden_sich(tmp_path):
    spec = importlib.util.spec_from_file_location("recognize_mod", SCRIPT)
    mod = importlib.util.module_from_spec(spec); spec.loader.exec_module(mod)
    strokes = [{"c": "#000", "w": 2, "p": [[0, 0, .5, 0], [40, 80, .5, 5], [80, 0, .5, 9]]}]
    a = mod.rasterize(strokes, *mod.VARIANTS[0])
    b = mod.rasterize(strokes, *mod.VARIANTS[1])
    assert a.size != b.size and len(mod.VARIANTS) == 2
    assert mod.unique_nonempty([" a ", "a", "", "b", "b "]) == ["a", "b"]


def test_debug_bild_wird_geschrieben_und_fehler_bleibt_sauber(tmp_path):
    if importlib.util.find_spec("texteller") is not None:
        pytest.skip("texteller ist installiert")
    f = _strokes_file(tmp_path, [{"c": "#000", "w": 2, "p": [[0, 0, .5, 0], [30, 40, .5, 3]]}])
    dbg = tmp_path / "sub" / "d.png"
    code, stdout, _ = _run(f, "--debug-image", dbg)
    assert code == 2 and "texteller" in json.loads(stdout)["error"]
    assert dbg.exists()          # das Eingabebild wurde trotzdem zur Fehlersuche gespeichert


def _load_mod():
    spec = importlib.util.spec_from_file_location("recognize_mod2", SCRIPT)
    mod = importlib.util.module_from_spec(spec); spec.loader.exec_module(mod)
    return mod


def _serve(mod, lines, loader):
    import io
    out = io.StringIO()
    code = mod.serve("test", loader, stdin=io.StringIO("\n".join(lines) + "\n"), stdout=out)
    return code, [json.loads(l) for l in out.getvalue().splitlines()]


GOOD = {"c": "#000", "w": 2, "p": [[0, 0, .5, 0], [30, 40, .5, 3]]}


def test_serve_protokoll_mehrere_anfragen_modell_einmal_geladen():
    mod = _load_mod()
    loads = []

    def loader():
        loads.append(1)
        return (lambda images: [f"v{len(images)}", "v2", "  "]), "9.9"

    reqs = [json.dumps({"id": 1, "strokes": [GOOD]}), json.dumps({"id": 2, "strokes": [GOOD]})]
    code, resp = _serve(mod, reqs, loader)
    assert code == 0 and len(loads) == 1                      # Modell nur einmal geladen
    assert resp[0] == {"ready": True, "engine": "test", "engine_version": "9.9"}
    assert [r["id"] for r in resp[1:]] == [1, 2]
    assert [c["latex"] for c in resp[1]["candidates"]] == ["v2"]   # Duplikate/Leeres entfernt


def test_serve_fehler_einer_anfrage_beendet_dienst_nicht():
    mod = _load_mod()
    loader = lambda: ((lambda images: ["ok"]), "1")
    reqs = [json.dumps({"id": 1, "strokes": []}),               # leer -> Fehler
            "kein json",
            json.dumps({"id": 3, "strokes": [GOOD]})]
    code, resp = _serve(mod, reqs, loader)
    assert code == 0
    assert "error" in resp[1] and resp[1]["id"] == 1
    assert resp[2] == {"error": "Ungueltige Anfrage"}
    assert resp[3]["candidates"][0]["latex"] == "ok"           # danach geht es weiter


def test_serve_debug_bild_und_ladefehler(tmp_path):
    mod = _load_mod()
    dbg = tmp_path / "x" / "d.png"
    code, resp = _serve(mod, [json.dumps({"id": 1, "strokes": [GOOD], "debug_image": str(dbg)})],
                        lambda: ((lambda images: ["a"]), "1"))
    assert dbg.exists()

    def broken():
        raise ModuleNotFoundError("Fehlt", name="texteller")
    code, resp = _serve(mod, [], broken)
    assert code == 2 and resp[0]["ready"] is False and "texteller" in resp[0]["error"]
