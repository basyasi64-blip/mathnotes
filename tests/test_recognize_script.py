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
