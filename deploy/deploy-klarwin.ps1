# Deploy KlarWin downloads + GanzSoft landing (KlarWin page/menu)
# Requires: SSH allinkl-ganzom reachable (Port 22)
# KAS: Subdomain dg.ganz-soft.de → Ordner /dg.ganz-soft.de/ anlegen, falls noch nicht vorhanden.

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $Root "deploy\dg.ganz-soft.de\klarwin\KlarWin-Setup.zip"))) {
  $Root = "C:\Users\dietr\Projects\klarwin"
}

$RemoteBase = "www/htdocs/w0217246"
$KlarWinRemote = "$RemoteBase/dg.ganz-soft.de/klarwin"
$LandingLocal = "C:\Users\dietr\Projects\ganz-soft-landing"
$KlarWinLocal = Join-Path $Root "deploy\dg.ganz-soft.de\klarwin"

Write-Host "1) Ordner dg.ganz-soft.de/klarwin anlegen ..."
ssh allinkl-ganzom "mkdir -p $KlarWinRemote && mkdir -p $RemoteBase/ganz-soft.de/klarwin && mkdir -p $RemoteBase/ganz-soft.de/bin"

Write-Host "2) Installer + PDF hochladen ..."
scp -r "$KlarWinLocal\*" "allinkl-ganzom:$KlarWinRemote/"

Write-Host "3) Marketing-Dateien KlarWin (static) ..."
scp -r "$LandingLocal\klarwin\*" "allinkl-ganzom:$RemoteBase/ganz-soft.de/klarwin/"
if (Test-Path "$LandingLocal\assets\style.css") {
  ssh allinkl-ganzom "mkdir -p $RemoteBase/ganz-soft.de/assets"
  scp "$LandingLocal\assets\style.css" "allinkl-ganzom:$RemoteBase/ganz-soft.de/assets/style.css"
}

$Seed = "C:\Users\dietr\Projects\DG\bin\seed-klarwin-page.php"
if (Test-Path $Seed) {
  Write-Host "4) Seed-Skript + CRM-Seite/Menü ..."
  scp $Seed "allinkl-ganzom:$RemoteBase/ganz-soft.de/bin/seed-klarwin-page.php"
  ssh allinkl-ganzom "cd $RemoteBase/ganz-soft.de && php bin/seed-klarwin-page.php"
} else {
  Write-Host "4) Seed-Skript fehlt lokal — Menü bitte manuell oder nach DG-Deploy."
}

Write-Host "Fertig."
Write-Host "  Download: https://dg.ganz-soft.de/klarwin/"
Write-Host "  Seite:    https://ganz-soft.de/klarwin/"
Write-Host ""
Write-Host "Falls dg.ganz-soft.de noch nicht erreichbar: In KAS Subdomain dg.ganz-soft.de auf Ordner /dg.ganz-soft.de/ legen."
Write-Host "Hinweis: seed-klarwin-page.php muss im CRM liegen (Repo DG → deploy)."
