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

## Korrektur v0.2: Stifteingabe
WinUI 3 besitzt keinen `InkCanvas` (der ist UWP-only). v0.2 erfasst Striche deshalb direkt ueber
Pointer-Ereignisse (`PointerPoint.Properties.Pressure`) und zeichnet sie als `Polyline`.
Neigung, Palm Rejection, Radierer, Lasso und niedrige Latenz (Win2D/Direct2D) folgen spaeter.

## Windows-Schicht (windows/)
- `MathNotes.Core` (net8.0): `NotebookStore` mit demselben SQLite-Schema wie der Python-Kern.
- `MathNotes.Core.Tests` (xUnit): Roundtrip, Unveraenderlichkeit, Verlauf.
- `MathNotes.App` (WinUI 3, unpackaged, self-contained): Oberflaeche; speichert jeden Strich sofort.
Die App haengt in v0.2 nicht von Python ab. Der PDF-Export laeuft vorerst ueber den Python-Kern (CLI).

## Erkennung (v0.4)
Windows 11 (Build 26200) hat keinen Mathe-Erkenner (per `math-probe.ps1` geprueft). Daher pix2tex als lokaler Sidecar:
`MainWindow` -> `Pix2TexClient` (C#) -> Prozess `recognizer/recognize.py` in einer eigenen venv
(`%LOCALAPPDATA%\MathNotes\recognizer\venv`, Einrichtung per `setup-recognizer.ps1` nach Nutzerzustimmung).
Striche werden als JSON uebergeben und in Python (Pillow) zu einem Bild gerastert; Ausgabe ist eine JSON-Zeile.
- Ergebnis ist immer ein *ungeprueft* gekennzeichneter Vorschlag; Uebernahme nur durch den Nutzer.
- Ein vorhandener Nutzertext (`edited_latex`) wird nie ueberschrieben, jede Erkennung steht im Versionsverlauf.
- pix2tex liefert keinen Konfidenzwert (`confidence` = NULL). Es ist auf gedruckte Formeln trainiert; Handschrift-Genauigkeit offen.
- Fehler/Absturz/Zeitueberschreitung des Erkenners werden als Fehlermeldung gezeigt und blockieren die App nicht.
- Modell wird pro Aufruf geladen (Wartezeit von Sekunden); ein dauerhafter Dienst waere eine spaetere Optimierung.
