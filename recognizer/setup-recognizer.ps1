# MathNotes: richtet die lokale Formelerkennung (TexTeller, auch fuer Handschrift) ein.
# Legt eine eigene Python-Umgebung unter %LOCALAPPDATA%\MathNotes\recognizer\venv an.
# Downloads: Python-Pakete von pypi.org (u. a. torch, texteller), Modellgewichte (ca. 1,2 GB) von huggingface.co.
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
    if ($v -match "Python 3\.(\d+)") {
      if ([int]$Matches[1] -lt 10) { Write-Host "Zu alt: $cand -> $v (TexTeller braucht Python 3.10 oder neuer)"; continue }
      $py = $cand; Write-Host "Gefunden: $cand -> $v"; break
    }
  } catch { }
}
if (-not $py) { Write-Host "Python 3.10 oder neuer nicht gefunden. Bitte von python.org installieren."; Read-Host "Enter zum Beenden"; exit 1 }

if (-not (Test-Path (Join-Path $venv "Scripts\python.exe"))) {
  Write-Host "Erstelle virtuelle Umgebung ..."
  Invoke-Expression "$py -m venv `"$venv`""
}
$vpy = Join-Path $venv "Scripts\python.exe"

Write-Host "Installiere texteller (grosser Download, kann einige Minuten dauern) ..."
& $vpy -m pip install --upgrade pip
& $vpy -m pip install texteller
if ($LASTEXITCODE -ne 0) { Write-Host "FEHLER bei der Installation. Bitte Ausgabe oben kopieren."; Read-Host "Enter zum Beenden"; exit 1 }

Write-Host "Lade Modellgewichte (einmalig, ca. 1,2 GB) ..."
& $vpy -c "from texteller import load_model, load_tokenizer; load_model(use_onnx=False); load_tokenizer(); print('Modell bereit')"
if ($LASTEXITCODE -ne 0) { Write-Host "FEHLER beim Modell-Download. Bitte Ausgabe oben kopieren."; Read-Host "Enter zum Beenden"; exit 1 }

Write-Host ""
Write-Host "Fertig. Du kannst dieses Fenster schliessen und in MathNotes 'Erkennen' benutzen."
Read-Host "Enter zum Beenden"
