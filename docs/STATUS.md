# Implementierungsstatus (ehrlich)

| Funktion | Status |
|---|---|
| SQLite-Speichern/Laden, Neustart-Roundtrip | Vollstaendig implementiert und getestet (Linux) |
| Unveraenderliche Originalstriche | Vollstaendig implementiert und getestet |
| Versionsverlauf Formeln, Nutzerkorrektur | Vollstaendig implementiert und getestet |
| Backup + Integritaetspruefung | Vollstaendig implementiert und getestet (unverschluesselt) |
| LaTeX-/PDF-Export | Vollstaendig implementiert und getestet (pdflatex, Linux) |
| Erkenner-Schnittstelle | Implementiert und getestet; **kein echtes Modell** (NullRecognizer) |
| Handschrifterkennung Mathematik: Windows-eigener Mathe-Erkenner | Nicht moeglich (auf Windows 11 Build 26200 nicht vorhanden, per Skript geprueft) |
| Handschrifterkennung Mathematik: pix2tex (lokal, Python-Sidecar) | Im Praxistest (Nutzer): "f(x)" wurde als "nabla(s)" erkannt, also fuer Handschrift unbrauchbar; als Alternative im Skript behalten |
| Handschrifterkennung Mathematik: TexTeller 3 (lokal, Python-Sidecar, fuer Handschrift trainiert) | Implementiert nach API-Dokumentation (Kandidaten aus zwei Darstellungen, Eingabebild als Debug-Datei); **nie ausgefuehrt, Genauigkeit unbekannt** |
| (alt) pix2tex-Eintrag | Implementiert (Strich-Rasterisierung, Aufruf, Parser, Speicherung, Einrichtungsskript); Rasterisierung/Parser/Fehlerpfade getestet; **echter Modelllauf und Genauigkeit bei Handschrift ungetestet** |
| WinUI-3-Oberflaeche (Strich-Canvas mit Druck, Original links, LaTeX-Feld rechts, Sofort-Speichern) | CI-Build gruen; vom Nutzer manuell geprueft: Start, Stiftschreiben, Speichern-Button |
| C#-Speicherschicht (gleiches SQLite-Schema) + xUnit-Tests | Implementiert; xUnit-Tests laufen in der CI |
| Radierer (Strich-Radierer, Stift-Radierende), Rueckgaengig, Alles loeschen (Soft-Delete, Original bleibt, wiederherstellbar) | Implementiert; Speicher-/Geometrie-Logik getestet (Python lokal, C# in CI), Bedienung ungetestet |
| Neigung, Palm Rejection, Lasso, Farben, Strichstaerken | Noch nicht implementiert |
| Gerenderte Formelvorschau (KaTeX 0.16.11 offline in WebView2) | Implementiert; KaTeX-Rendering per node getestet, WebView2-Anbindung noch ungetestet |
| Windows-Installer, Portable Version | Noch nicht implementiert |
| Windows-CI (Python-Tests, C#-Tests, App-Publish als portable Version) | Vorbereitet, **noch nie gelaufen** |
| Verschluesselung, Auto-Backup, Wissensnetz, Simulation u. a. | Noch nicht implementiert |

## v0.7: mehrere Formeln und Live-Erkennung
| Funktion | Status |
|---|---|
| Striche zu Formeln gruppieren (neben-/untereinander, Lesereihenfolge) | Python-Referenz getestet (8 Faelle); C#-Port mit gleichen Tests, laeuft erst in der CI |
| Zuordnung Gruppen <-> Formeln (Id und Nutzerkorrektur bleiben beim Weiterschreiben erhalten) | Python getestet; C#-Port mit gleichen Tests, laeuft erst in der CI |
| Erkenner als Dauerprozess (Modell einmal laden, Protokoll per JSON-Zeilen) | Protokoll und Fehlerverhalten getestet (mit Test-Erkenner); **echtes TexTeller-Modell hier nie gelaufen** |
| Live-Erkennung (Pause 1 s nach dem Schreiben, nur geaenderte Formeln) | Implementiert, **Bedienung ungetestet** |
| Formelliste mit gerenderter Vorschau, Auswahl, Korrektur, Kandidaten; gestrichelte Rahmen mit Nummern auf der Schreibflaeche | Implementiert, **ungetestet** (C#/WinUI nur in der CI kompiliert) |
| PDF/LaTeX-Export: sichtbare Formeln in Lesereihenfolge | Python getestet; der Export ist weiterhin nur per Kommandozeile erreichbar, nicht aus der App |
| Schreibflaeche = sichtbarer Fensterbereich (App startet maximiert); kein Scrollen/Zoomen | Bekannte Einschraenkung |

## v0.8: Text, Ausrichtung, Praesentation
| Funktion | Status |
|---|---|
| Blocktyp Formel/Text (Schreibmodus fuer neue Bloecke, Typ pro Block aenderbar; Typwechsel leert den Inhalt, Verlauf bleibt) | Speicherlogik getestet (C#-Tests laufen in der CI); Bedienung ungetestet |
| Normaler Text: Windows-Handschrifterkennung (Deutsch bevorzugt), lokal, ohne Einrichtung | Implementiert nach Windows-API-Dokumentation, **nie ausgefuehrt, Genauigkeit unbekannt** |
| Ausrichtung links/Mitte/rechts: automatisch aus der Schreibposition (Drittel der Seitenbreite), pro Block von Hand aenderbar | Logik getestet (CI); Bedienung ungetestet |
| Praesentationsfenster (gesetzte Seite, gross, live; mehrere Bloecke je Zeile mit gemischter Ausrichtung; Schriftgroesse A-/A+; Vollbild per Knopf oder F11) | Datenaufbau getestet (CI); Darstellung/Fenster **ungetestet** |
| PDF/LaTeX-Export beruecksichtigt Text-Bloecke und Ausrichtung | Noch nicht implementiert (Export ist ohnehin nur per Kommandozeile erreichbar) |

Nachtrag v0.8.1: Praesentationsfenster zeigt nur erkannte/korrigierte Inhalte (keine Nummern, keine Handschrift, keine Platzhalter);
aufeinanderfolgende Text-Bloecke einer Zeile werden zu einer Textzeile verbunden (Test in der CI).
