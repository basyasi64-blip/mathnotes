import json
import shutil
import subprocess
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
PLOT = ROOT / "windows" / "MathNotes.App" / "Assets" / "preview" / "plot.js"
NODE = shutil.which("node")
HAVE_TEX = shutil.which("pdflatex") and subprocess.run(["kpsewhich", "pgfplots.sty"], capture_output=True, text=True).stdout.strip()

pytestmark = pytest.mark.skipif(not (NODE and HAVE_TEX), reason="node oder pgfplots nicht installiert")

CASES = [
    ("f(x)=x^{2}-2", {}),
    ("f(x)=\\sin x, g(x)=\\cos x", {"xmin": -7, "xmax": 7, "ymin": -2, "ymax": 2}),
    ("\\frac{1}{x}", {}),
    ("\\tan x", {"xmin": -4, "xmax": 4, "ymin": -4, "ymax": 4}),
    ("e^{-x^{2}}, \\sqrt{x+1}, |x|-1, \\ln x", {}),
    ("\\sin^2 x + \\cos^2 x", {}),
    ("\\frac{x^2-1}{x+1}", {"xmin": -3, "xmax": 3}),
    ("\\log_{2} x", {"xmin": 0.1, "xmax": 8}),
]


def tikz_for(expr, props):
    js = (f"const P=require({json.dumps(str(PLOT))});"
          f"process.stdout.write(P.graphToTikz({json.dumps(expr)},{json.dumps(props)}));")
    return subprocess.run([NODE, "-e", js], capture_output=True, text=True, check=True).stdout


@pytest.mark.parametrize("expr,props", CASES)
def test_erzeugter_tikz_code_kompiliert_wirklich(expr, props, tmp_path):
    tikz = tikz_for(expr, props)
    (tmp_path / "t.tex").write_text(
        "\\documentclass{article}\n\\usepackage{pgfplots}\\pgfplotsset{compat=1.18}\n"
        "\\begin{document}\n" + tikz + "\n\\end{document}\n", encoding="utf-8")
    r = subprocess.run(["pdflatex", "-interaction=nonstopmode", "-halt-on-error", "t.tex"],
                       cwd=tmp_path, capture_output=True, text=True, timeout=120)
    assert r.returncode == 0 and (tmp_path / "t.pdf").exists(), r.stdout[-1500:]


def test_ungueltiger_ausdruck_gibt_fehler_statt_tikz():
    js = (f"const P=require({json.dumps(str(PLOT))});"
          "try{P.graphToTikz('x^2+y^2=1',{});console.log('KEIN FEHLER')}catch(e){console.log('Fehler: '+e.message)}")
    out = subprocess.run([NODE, "-e", js], capture_output=True, text=True).stdout
    assert out.startswith("Fehler:")
