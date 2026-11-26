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

## Korrektur v0.6: Erkenner
Praxistest: pix2tex erkennt Handschrift nicht (f(x) -> nabla(s)). Standard ist jetzt TexTeller 3 (`pip install texteller`,
laut Projekt fuer Handschrift trainiert; Modell ca. 1,2 GB von huggingface.co). `recognize.py --engine pix2tex` bleibt waehlbar.
Dieselben Striche werden in zwei Darstellungen (duenn/dick, supersampled) erkannt; unterschiedliche Ergebnisse erscheinen als Kandidatenliste.
Das Eingabebild liegt in `%LOCALAPPDATA%\MathNotes\last_recognition.png` zur Fehlersuche.
Falls auch TexTeller bei Handschrift scheitert, waeren naechste Optionen: Strich-basierte Erkenner (nutzen Zeitverlauf) oder ein
freiwillig zuschaltbarer Online-Dienst mit ausdruecklicher Zustimmung (widerspricht dem Offline-Standard).

## v0.7: Mehrere Formeln, Live-Erkennung
- `StrokeGrouping` (C#, Referenz `segmentation.py`): Striche -> Gruppen. Regel: Umrandungen horizontal <= 1,5 s und vertikal <= 0,5 s
  auseinander (s = Median der Strichgroessen). Zwei Formeln brauchen also etwas Abstand. Matrizen mit grossem Zeilenabstand koennen zerfallen.
- `NotebookStore.SyncFormulas`: Gruppen werden per groesster Ueberlappung bestehenden Formeln zugeordnet; neue Striche aendern Id und
  Nutzerkorrektur nicht. Spalte `recognized_ids` merkt, mit welchen Strichen zuletzt erkannt wurde (Migration beim Oeffnen).
  Formeln ohne Gruppe bleiben in der Datenbank, erscheinen aber nicht mehr.
- `RecognizerService`: `recognize.py --serve` als Dauerprozess, Anfragen/Antworten als JSON-Zeilen mit Id.
  Ein Fehler/Absturz wird als Fehlermeldung gezeigt; der naechste Aufruf startet den Prozess neu.
- Live-Ablauf: 1 s Pause nach dem letzten Strich -> gruppieren -> synchronisieren -> nur Formeln mit geaenderten Strichen erkennen
  (nacheinander). Wird waehrenddessen weitergeschrieben, bricht die Schleife ab und die Analyse startet neu.
  Fehlgeschlagene Erkennungen werden fuer dieselben Striche nicht automatisch wiederholt ('Jetzt erkennen' setzt das zurueck).
- Ergebnisse sind immer 'ungeprueft'. Eine Korrektur des Nutzers (`edited_latex`) wird nie ueberschrieben; sie bleibt auch,
  wenn spaeter Striche zur Formel hinzukommen (dann kann sie veraltet sein; es gibt dafuer noch keine Warnung).
