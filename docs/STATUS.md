# Implementierungsstatus (ehrlich)

| Funktion | Status |
|---|---|
| SQLite-Speichern/Laden, Neustart-Roundtrip | Vollstaendig implementiert und getestet (Linux) |
| Unveraenderliche Originalstriche | Vollstaendig implementiert und getestet |
| Versionsverlauf Formeln, Nutzerkorrektur | Vollstaendig implementiert und getestet |
| Backup + Integritaetspruefung | Vollstaendig implementiert und getestet (unverschluesselt) |
| LaTeX-/PDF-Export | Vollstaendig implementiert und getestet (pdflatex, Linux) |
| Erkenner-Schnittstelle | Implementiert und getestet; **kein echtes Modell** (NullRecognizer) |
| Handschrifterkennung Mathematik | Noch nicht implementiert |
| WinUI-3-Oberflaeche (Strich-Canvas mit Druck, Original links, LaTeX-Feld rechts, Sofort-Speichern) | Implementiert, **noch nie kompiliert oder ausgefuehrt** (kein Windows hier) |
| C#-Speicherschicht (gleiches SQLite-Schema) + xUnit-Tests | Implementiert, **Tests noch nie gelaufen** (kein .NET hier) |
| Stiftspezifisch: Neigung, Palm Rejection, Radierer, Lasso, Farben | Noch nicht implementiert |
| Gerenderte Formelvorschau (KaTeX/WebView2) | Noch nicht implementiert |
| Windows-Installer, Portable Version | Noch nicht implementiert |
| Windows-CI (Python-Tests, C#-Tests, App-Publish als portable Version) | Vorbereitet, **noch nie gelaufen** |
| Verschluesselung, Auto-Backup, Wissensnetz, Simulation u. a. | Noch nicht implementiert |
