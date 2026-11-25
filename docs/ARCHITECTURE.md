# Architektur (Stand v0.1)

Prinzip: einfachste Architektur je Modul, lokal/offline, Originalstriche unveraenderlich.

| Modul | Technologie | Aufgabe |
|---|---|---|
| `mathnotes.storage` | SQLite (WAL) | Notizbuecher, Seiten, Originalstriche (Trigger-geschuetzt), Formeln, Versionsverlauf, Backup, Integritaetspruefung |
| `mathnotes.recognition` | Python-Protocol | Austauschbare Erkenner (`Recognizer`); `NullRecognizer` erkennt bewusst nichts |
| `mathnotes.export` | LaTeX (pdflatex) | .tex-Erzeugung, echte PDF-Kompilierung, Fehler werden gemeldet |
| `mathnotes.cli` | argparse | Demo des Kernablaufs ohne Stift |
| UI (geplant) | WinUI 3, InkCanvas, WebView2+KaTeX | Stifteingabe, Original/LaTeX nebeneinander; spricht den Kern ueber lokale Schnittstelle (Named Pipe/gRPC) an |

Drei verknuepfte Repraesentationen pro Formel: Originalstriche (`strokes`) -> erkanntes LaTeX
(`formulas.recognized_latex`) -> Nutzerkorrektur (`formulas.edited_latex`). Das Erkenner-Ergebnis
wird nie ueberschrieben; jede Aenderung landet in `formula_versions`.

Entscheidungen: SQLite statt Dateiformat-Eigenbau (robust, atomar); pdflatex jetzt, Tectonic spaeter
fuer die Windows-Auslieferung zu pruefen; Erkenner-Modell (z. B. pix2tex/ONNX) ist noch nicht integriert.
