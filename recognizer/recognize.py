#!/usr/bin/env python
"""MathNotes: lokale Handschrift-/Formelerkennung (pix2tex), Aufruf aus der App.

Aufruf:   python recognize.py strokes.json
          python recognize.py strokes.json --rasterize-only bild.png   (ohne Modell, zum Testen)
Eingabe:  {"strokes":[{"c":"#000000","w":2.0,"p":[[x,y,druck,t_ms],...]}, ...]}
Ausgabe:  eine JSON-Zeile auf stdout:
          {"engine":"pix2tex","engine_version":"...","candidates":[{"latex":"...","confidence":null}]}
          bei Fehler: {"error":"..."} und Exit-Code 2.
Es werden KEINE Daten ins Internet gesendet. Nur beim allerersten Start laedt pix2tex
einmalig seine Modellgewichte von GitHub herunter.
Hinweis: pix2tex liefert keinen Konfidenzwert, deshalb ist "confidence" immer null.
"""
from __future__ import annotations
import contextlib
import json
import sys

PADDING = 24
TARGET_HEIGHT = 128   # Zielhoehe der Formel im Bild (Pixel)
MAX_WIDTH = 1200


def load_strokes(path: str) -> list[dict]:
    with open(path, encoding="utf-8") as f:
        data = json.load(f)
    strokes = data.get("strokes", [])
    return [s for s in strokes if s.get("p")]


def rasterize(strokes: list[dict]):
    """Striche -> weisses PIL-Bild mit schwarzer Schrift, passend skaliert."""
    from PIL import Image, ImageDraw
    if not strokes:
        raise ValueError("keine Striche vorhanden")
    xs = [p[0] for s in strokes for p in s["p"]]
    ys = [p[1] for s in strokes for p in s["p"]]
    minx, maxx, miny, maxy = min(xs), max(xs), min(ys), max(ys)
    w, h = max(maxx - minx, 1.0), max(maxy - miny, 1.0)
    scale = min(TARGET_HEIGHT / h, (MAX_WIDTH - 2 * PADDING) / w)
    scale = max(scale, 0.05)
    W = int(w * scale) + 2 * PADDING
    H = int(h * scale) + 2 * PADDING
    img = Image.new("RGB", (max(W, 2 * PADDING + 1), max(H, 2 * PADDING + 1)), "white")
    d = ImageDraw.Draw(img)
    line_w = max(3, int(TARGET_HEIGHT / 24))
    for s in strokes:
        pts = [((p[0] - minx) * scale + PADDING, (p[1] - miny) * scale + PADDING) for p in s["p"]]
        if len(pts) == 1:
            x, y = pts[0]
            d.ellipse([x - line_w / 2, y - line_w / 2, x + line_w / 2, y + line_w / 2], fill="black")
        else:
            d.line(pts, fill="black", width=line_w, joint="curve")
            for x, y in (pts[0], pts[-1]):
                d.ellipse([x - line_w / 2, y - line_w / 2, x + line_w / 2, y + line_w / 2], fill="black")
    return img


def run_model(img) -> tuple[str, str]:
    # pix2tex schreibt Fortschritt/Meldungen auf stdout -> umleiten, damit stdout sauber bleibt
    with contextlib.redirect_stdout(sys.stderr):
        from pix2tex.cli import LatexOCR
        model = LatexOCR()
        latex = model(img)
    try:
        from importlib.metadata import version
        ver = version("pix2tex")
    except Exception:
        ver = "unbekannt"
    return str(latex), ver


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        print(json.dumps({"error": "Aufruf: recognize.py strokes.json [--rasterize-only out.png]"}))
        return 2
    try:
        strokes = load_strokes(argv[1])
        img = rasterize(strokes)
        if len(argv) >= 4 and argv[2] == "--rasterize-only":
            img.save(argv[3])
            print(json.dumps({"rasterized": argv[3], "size": list(img.size)}))
            return 0
        latex, ver = run_model(img)
        cands = [{"latex": latex, "confidence": None}] if latex.strip() else []
        print(json.dumps({"engine": "pix2tex", "engine_version": ver, "candidates": cands}))
        return 0
    except ModuleNotFoundError as e:
        print(json.dumps({"error": f"Komponente fehlt: {e.name}. Bitte 'Erkennung einrichten' ausfuehren."}))
        return 2
    except Exception as e:  # Fehler des Erkenners duerfen die App nie blockieren
        print(json.dumps({"error": f"{type(e).__name__}: {e}"}))
        return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
