"""LaTeX-Erzeugung und echte PDF-Kompilierung (latexmk/pdflatex aus TeX Live)."""
from __future__ import annotations
import shutil
import subprocess
import tempfile
from pathlib import Path
from .storage import Store

_ESC = {"&": r"\&", "%": r"\%", "$": r"\$", "#": r"\#", "_": r"\_",
        "{": r"\{", "}": r"\}", "~": r"\textasciitilde{}", "^": r"\textasciicircum{}",
        "\\": r"\textbackslash{}"}


def escape_text(s: str) -> str:
    return "".join(_ESC.get(c, c) for c in s)


def build_tex(store: Store, notebook_id: int) -> str:
    title = dict(store.notebooks())[notebook_id]
    lines = [r"\documentclass[a4paper,11pt]{article}",
             r"\usepackage[utf8]{inputenc}", r"\usepackage[T1]{fontenc}",
             r"\usepackage{amsmath,amssymb}",
             r"\title{" + escape_text(title) + "}", r"\date{}",
             r"\begin{document}", r"\maketitle"]
    for n, page_id in enumerate(store.pages(notebook_id), 1):
        lines.append(rf"\section*{{Seite {n}}}")
        for f in store.formulas(page_id):
            if f.current_latex is None:
                lines.append(r"\textit{[Formel nicht erkannt und nicht bearbeitet]}\par")
            else:
                lines.append(r"\begin{equation*}" + "\n" + f.current_latex + "\n" + r"\end{equation*}")
    lines.append(r"\end{document}")
    return "\n".join(lines) + "\n"


def compile_pdf(tex: str, out_pdf: str | Path) -> tuple[bool, str]:
    """Kompiliert echt. Rueckgabe: (Erfolg, Log-Auszug). Kein Erfolg wird vorgetaeuscht."""
    engine = shutil.which("pdflatex")
    if engine is None:
        return False, "pdflatex nicht gefunden"
    with tempfile.TemporaryDirectory() as d:
        src = Path(d) / "doc.tex"
        src.write_text(tex, encoding="utf-8")
        p = subprocess.run([engine, "-interaction=nonstopmode", "-halt-on-error", "doc.tex"],
                           cwd=d, capture_output=True, text=True, timeout=120)
        pdf = Path(d) / "doc.pdf"
        if p.returncode == 0 and pdf.exists():
            shutil.copyfile(pdf, out_pdf)
            return True, ""
        return False, (p.stdout or "")[-2000:]
