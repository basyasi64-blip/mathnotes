#!/usr/bin/env python
"""MathNotes: lokale Handschrift-/Formelerkennung, Aufruf aus der App.

Aufruf:
  python recognize.py strokes.json [--engine texteller|pix2tex] [--debug-image bild.png]
  python recognize.py strokes.json --rasterize-only bild.png      (ohne Modell, zum Testen)
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


def run_texteller(images) -> tuple[list[str], str]:
    # Bibliotheken schreiben Meldungen auf stdout -> umleiten, damit stdout sauber bleibt
    with contextlib.redirect_stdout(sys.stderr):
        import numpy as np
        import torch
        from texteller import load_model, load_tokenizer, img2latex
        model = load_model(use_onnx=False)
        tokenizer = load_tokenizer()
        device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
        res = img2latex(model, tokenizer, [np.array(im) for im in images],
                        device=device, out_format="katex")
    return [str(r) for r in res], _version("texteller")


def run_pix2tex(images) -> tuple[list[str], str]:
    with contextlib.redirect_stdout(sys.stderr):
        from pix2tex.cli import LatexOCR
        model = LatexOCR()
        res = [model(im) for im in images]
    return [str(r) for r in res], _version("pix2tex")


ENGINES = {"texteller": run_texteller, "pix2tex": run_pix2tex}


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
    ap.add_argument("strokes")
    ap.add_argument("--engine", default="texteller", choices=sorted(ENGINES))
    ap.add_argument("--debug-image")
    ap.add_argument("--rasterize-only")
    try:
        a = ap.parse_args(argv[1:])
    except SystemExit:
        print(json.dumps({"error": "Aufruf: recognize.py strokes.json [--engine texteller|pix2tex] [--debug-image pfad.png]"}))
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
        latex_list, ver = ENGINES[a.engine](images)
        cands = [{"latex": t, "confidence": None} for t in unique_nonempty(latex_list)]
        print(json.dumps({"engine": a.engine, "engine_version": ver, "candidates": cands}))
        return 0
    except ModuleNotFoundError as e:
        print(json.dumps({"error": f"Komponente fehlt: {e.name} (Erkennung '{a.engine}'). Bitte 'Erkennung einrichten' ausfuehren."}))
        return 2
    except Exception as e:  # Fehler des Erkenners duerfen die App nie blockieren
        print(json.dumps({"error": f"{type(e).__name__}: {e}"}))
        return 2


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
