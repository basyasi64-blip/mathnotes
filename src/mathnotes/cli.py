"""Kommandozeilen-Demo des Kernablaufs (ersetzt NICHT die Stift-Oberflaeche)."""
from __future__ import annotations
import argparse
from pathlib import Path
from .storage import Store
from .models import Stroke, Point
from . import recognition, export


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="mathnotes")
    ap.add_argument("db")
    sub = ap.add_subparsers(dest="cmd", required=True)
    n = sub.add_parser("new"); n.add_argument("title")
    sub.add_parser("list")
    e = sub.add_parser("edit"); e.add_argument("formula_id", type=int); e.add_argument("latex")
    x = sub.add_parser("export"); x.add_argument("notebook_id", type=int); x.add_argument("outdir")
    a = ap.parse_args(argv)
    s = Store(a.db)
    try:
        if a.cmd == "new":
            nb = s.create_notebook(a.title)
            page = s.pages(nb)[0]
            # Demo-Strich: kein Stift verfuegbar -> synthetischer Strich, vom NullRecognizer NICHT erkannt
            ids = s.add_strokes(page, [Stroke((Point(0, 0), Point(1, 1)))])
            res = recognition.get("null").recognize([st for _, st in s.strokes(page)])
            s.add_formula(page, ids, res)
            print(f"Notizbuch {nb} angelegt (Erkennung: {'ja' if res.recognized else 'nein, manuell korrigieren'})")
        elif a.cmd == "list":
            for i, t in s.notebooks():
                print(i, t)
        elif a.cmd == "edit":
            s.edit_formula(a.formula_id, a.latex); print("gespeichert")
        elif a.cmd == "export":
            out = Path(a.outdir); out.mkdir(parents=True, exist_ok=True)
            tex = export.build_tex(s, a.notebook_id)
            (out / "notebook.tex").write_text(tex, encoding="utf-8")
            ok, log = export.compile_pdf(tex, out / "notebook.pdf")
            print("PDF:", "ok" if ok else "FEHLER\n" + log)
            return 0 if ok else 1
    finally:
        s.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
