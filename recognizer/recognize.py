#!/usr/bin/env python
"""MathNotes: lokale Handschrift-/Formelerkennung, Aufruf aus der App.

Aufruf:
  python recognize.py strokes.json [--engine texteller|pix2tex] [--debug-image bild.png]
  python recognize.py strokes.json --rasterize-only bild.png      (ohne Modell, zum Testen)
  python recognize.py --serve [--engine ...]    Dauerbetrieb: Modell wird einmal geladen, dann pro Zeile
        eine JSON-Anfrage auf stdin {"id":1,"strokes":[...],"debug_image":"pfad"} und pro Anfrage
        eine JSON-Antwort auf stdout. Erste Zeile: {"ready":true,...} oder {"ready":false,"error":...}.
Eingabe:  {"strokes":[{"c":"#000000","w":2.0,"p":[[x,y,druck,t_ms],...]}, ...]}
Ausgabe:  eine JSON-Zeile auf stdout:
          {"engine":"texteller","engine_version":"...","candidates":[{"latex":"...","confidence":null}]}
          Bei Fehler: {"error":"..."} und Exit-Code 2.
Es werden KEINE Notizen oder Striche ins Internet gesendet. Nur beim ersten Start laedt die
Erkennungs-Bibliothek einmalig ihre Modellgewichte herunter.
Hinweis: Keine der Bibliotheken liefert einen Konfidenzwert, "confidence" ist immer null.
Mehrere Kandidaten entstehen, weil dieselben Striche in zwei Darstellungen (duenn/dick) erkannt werden.
"""
from __future__ import annotations
import argparse
import contextlib
import json
import os
import sys

PADDING = 24
MAX_WIDTH = 1400
SUPERSAMPLE = 3
# (Zielhoehe in Pixel, Strichbreite in Pixel) der beiden Darstellungen
VARIANTS = [(128, 4), (192, 6)]


def load_strokes(path: str) -> list[dict]:
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    return [s for s in data.get("strokes", []) if s.get("p")]


def rasterize(strokes: list[dict], target_height: int = 128, line_width: int = 4):
    """Striche -> weisses RGB-Bild mit weich gezeichneter schwarzer Schrift (Supersampling)."""
    from PIL import Image, ImageDraw
    if not strokes:
        raise ValueError("keine Striche vorhanden")
    xs = [p[0] for s in strokes for p in s["p"]]
    ys = [p[1] for s in strokes for p in s["p"]]
    minx, maxx, miny, maxy = min(xs), max(xs), min(ys), max(ys)
    w, h = max(maxx - minx, 1.0), max(maxy - miny, 1.0)
    scale = min(target_height / h, (MAX_WIDTH - 2 * PADDING) / w)
    scale = max(scale, 0.05)
    k = SUPERSAMPLE
    W = int(w * scale) + 2 * PADDING
    H = int(h * scale) + 2 * PADDING
    W, H = max(W, 2 * PADDING + 1), max(H, 2 * PADDING + 1)
    big = Image.new("L", (W * k, H * k), 255)
    d = ImageDraw.Draw(big)
    lw = max(1, line_width * k)
    r = lw / 2
    for s in strokes:
        pts = [(((p[0] - minx) * scale + PADDING) * k, ((p[1] - miny) * scale + PADDING) * k) for p in s["p"]]
        if len(pts) > 1:
            d.line(pts, fill=0, width=lw, joint="curve")
        for x, y in (pts[0], pts[-1]):
            d.ellipse([x - r, y - r, x + r, y + r], fill=0)
    return big.resize((W, H), Image.LANCZOS).convert("RGB")


def _version(pkg: str) -> str:
    try:
        from importlib.metadata import version
        return version(pkg)
    except Exception:
        return "unbekannt"


def load_texteller():
    """Laedt das Modell einmal; gibt (infer, version) zurueck. infer(images) -> Liste LaTeX-Texte."""
    with contextlib.redirect_stdout(sys.stderr):   # Bibliotheken schreiben auf stdout -> umleiten
        import numpy as np
        import torch
        from texteller import load_model, load_tokenizer, img2latex
        model = load_model(use_onnx=False)
        tokenizer = load_tokenizer()
        device = torch.device("cuda" if torch.cuda.is_available() else "cpu")

    def infer(images):
        with contextlib.redirect_stdout(sys.stderr):
            res = img2latex(model, tokenizer, [np.array(im) for im in images],
                            device=device, out_format="katex")
        return [str(r) for r in res]

    return infer, _version("texteller")


def load_pix2tex():
    with contextlib.redirect_stdout(sys.stderr):
        from pix2tex.cli import LatexOCR
        model = LatexOCR()

    def infer(images):
        with contextlib.redirect_stdout(sys.stderr):
            return [str(model(im)) for im in images]

    return infer, _version("pix2tex")


LOADERS = {"texteller": load_texteller, "pix2tex": load_pix2tex}
ENGINES = LOADERS   # Abwaertskompatibler Name


def candidates_from(latex_list) -> list[dict]:
    return [{"latex": t, "confidence": None} for t in unique_nonempty(latex_list)]


def serve(engine_name: str, loader, stdin=None, stdout=None) -> int:
    """Dauerbetrieb. `loader` liefert (infer, version); austauschbar fuer Tests."""
    stdin = stdin if stdin is not None else sys.stdin
    out = stdout if stdout is not None else sys.stdout

    def emit(obj) -> None:
        out.write(json.dumps(obj) + "\n")
        out.flush()

    try:
        infer, ver = loader()
    except ModuleNotFoundError as e:
        emit({"ready": False, "error": f"Komponente fehlt: {e.name} (Erkennung '{engine_name}'). Bitte 'Erkennung einrichten' ausfuehren."})
        return 2
    except Exception as e:
        emit({"ready": False, "error": f"{type(e).__name__}: {e}"})
        return 2
    emit({"ready": True, "engine": engine_name, "engine_version": ver})
    for line in stdin:
        line = line.strip()
        if not line:
            continue
        try:
            req = json.loads(line)
            rid = req.get("id")
        except Exception:
            emit({"error": "Ungueltige Anfrage"})
            continue
        try:
            strokes = [st for st in req.get("strokes", []) if st.get("p")]
            images = [rasterize(strokes, th, lw) for th, lw in VARIANTS]
            dbg = req.get("debug_image")
            if dbg:
                os.makedirs(os.path.dirname(os.path.abspath(dbg)), exist_ok=True)
                images[0].save(dbg)
            emit({"id": rid, "engine": engine_name, "engine_version": ver,
                  "candidates": candidates_from(infer(images))})
        except Exception as e:   # ein Fehler bei einer Anfrage beendet den Dienst nicht
            emit({"id": rid, "error": f"{type(e).__name__}: {e}"})
    return 0


def unique_nonempty(items: list[str]) -> list[str]:
    seen, out = set(), []
    for t in items:
        t = t.strip()
        if t and t not in seen:
            seen.add(t)
            out.append(t)
    return out


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(add_help=False)
    ap.add_argument("strokes", nargs="?")
    ap.add_argument("--serve", action="store_true")
    ap.add_argument("--engine", default="texteller", choices=sorted(ENGINES))
    ap.add_argument("--debug-image")
    ap.add_argument("--rasterize-only")
    try:
        a = ap.parse_args(argv[1:])
    except SystemExit:
        print(json.dumps({"error": "Aufruf: recognize.py strokes.json [--engine texteller|pix2tex] [--debug-image pfad.png] | --serve"}))
        return 2
    if a.serve:
        return serve(a.engine, LOADERS[a.engine])
    if not a.strokes:
        print(json.dumps({"error": "Striche-Datei fehlt"}))
        return 2
    try:
        strokes = load_strokes(a.strokes)
        images = [rasterize(strokes, th, lw) for th, lw in VARIANTS]
        if a.rasterize_only:
            images[0].save(a.rasterize_only)
            print(json.dumps({"rasterized": a.rasterize_only, "size": list(images[0].size)}))
            return 0
        if a.debug_image:
            os.makedirs(os.path.dirname(os.path.abspath(a.debug_image)), exist_ok=True)
            images[0].save(a.debug_image)
        infer, ver = LOADERS[a.engine]()
        print(json.dumps({"engine": a.engine, "engine_version": ver,
                          "candidates": candidates_from(infer(images))}))
        return 0
    except ModuleNotFoundError as e:
        print(json.dumps({"error": f"Komponente fehlt: {e.name} (Erkennung '{a.engine}'). Bitte 'Erkennung einrichten' ausfuehren."}))
        return 2
    except Exception as e:  # Fehler des Erkenners duerfen die App nie blockieren
        print(json.dumps({"error": f"{type(e).__name__}: {e}"}))
        return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
