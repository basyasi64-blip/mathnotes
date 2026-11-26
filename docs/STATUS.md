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
| Handschrifterkennung Mathematik: pix2tex (lokal, Python-Sidecar) | Implementiert (Strich-Rasterisierung, Aufruf, Parser, Speicherung, Einrichtungsskript); Rasterisierung/Parser/Fehlerpfade getestet; **echter Modelllauf und Genauigkeit bei Handschrift ungetestet** |
| WinUI-3-Oberflaeche (Strich-Canvas mit Druck, Original links, LaTeX-Feld rechts, Sofort-Speichern) | CI-Build gruen; vom Nutzer manuell geprueft: Start, Stiftschreiben, Speichern-Button |
| C#-Speicherschicht (gleiches SQLite-Schema) + xUnit-Tests | Implementiert; xUnit-Tests laufen in der CI |
| Stiftspezifisch: Neigung, Palm Rejection, Radierer, Lasso, Farben | Noch nicht implementiert |
| Gerenderte Formelvorschau (KaTeX 0.16.11 offline in WebView2) | Implementiert; KaTeX-Rendering per node getestet, WebView2-Anbindung noch ungetestet |
| Windows-Installer, Portable Version | Noch nicht implementiert |
| Windows-CI (Python-Tests, C#-Tests, App-Publish als portable Version) | Vorbereitet, **noch nie gelaufen** |
| Verschluesselung, Auto-Backup, Wissensnetz, Simulation u. a. | Noch nicht implementiert |
