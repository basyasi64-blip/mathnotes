# MathNotes: richtet die lokale Formelerkennung (pix2tex) ein.
# Legt eine eigene Python-Umgebung unter %LOCALAPPDATA%\MathNotes\recognizer\venv an.
# Downloads: Python-Pakete von pypi.org (u. a. torch, pix2tex), Modellgewichte von github.com.
# Es werden keine Notizen oder Striche uebertragen.
$ErrorActionPreference = "Stop"
$base = Join-Path $env:LOCALAPPDATA "MathNotes\recognizer"
$venv = Join-Path $base "venv"
New-Item -ItemType Directory -Force -Path $base | Out-Null

Write-Host "Suche Python ..."
$py = $null
foreach ($cand in @("py -3", "python")) {
  try {
    $v = Invoke-Expression "$cand --version" 2>&1
    if ($v -match "Python 3\.(\d+)") { $py = $cand; Write-Host "Gefunden: $cand -> $v"; break }
  } catch { }
}
if (-not $py) { Write-Host "Python 3 nicht gefunden. Bitte von python.org installieren."; Read-Host "Enter zum Beenden"; exit 1 }

if (-not (Test-Path (Join-Path $venv "Scripts\python.exe"))) {
  Write-Host "Erstelle virtuelle Umgebung ..."
  Invoke-Expression "$py -m venv `"$venv`""
}
$vpy = Join-Path $venv "Scripts\python.exe"

Write-Host "Installiere pix2tex (grosser Download, kann einige Minuten dauern) ..."
& $vpy -m pip install --upgrade pip
& $vpy -m pip install pix2tex
if ($LASTEXITCODE -ne 0) { Write-Host "FEHLER bei der Installation. Bitte Ausgabe oben kopieren."; Read-Host "Enter zum Beenden"; exit 1 }

Write-Host "Lade Modellgewichte (einmalig) ..."
& $vpy -c "from pix2tex.cli import LatexOCR; LatexOCR(); print('Modell bereit')"
if ($LASTEXITCODE -ne 0) { Write-Host "FEHLER beim Modell-Download. Bitte Ausgabe oben kopieren."; Read-Host "Enter zum Beenden"; exit 1 }

Write-Host ""
Write-Host "Fertig. Du kannst dieses Fenster schliessen und in MathNotes 'Erkennen' benutzen."
Read-Host "Enter zum Beenden"
