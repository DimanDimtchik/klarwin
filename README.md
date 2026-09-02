# KlarWin

Windows-Werkzeug von GanzSoft: aufräumen, sichern, Netzwerk (Router via UPnP/IGD) und KI-Last.

## Start (Entwicklung)

```powershell
dotnet run --project src/KlarWin/KlarWin.csproj
```

## Installation (Release)

Paket: `deploy/dg.ganz-soft.de/klarwin/KlarWin-Setup.zip`  
Handbuch: `deploy/dg.ganz-soft.de/klarwin/KlarWin-Handbuch.pdf`

```powershell
# Icon aus PNG erzeugen (nach Änderung an Assets/klarwin-icon.png):
python bin/build-icon.py

# Neu bauen + zippen, dann hochladen:
.\deploy\deploy-klarwin.ps1
```

Download-URL (nach Deploy): https://dg.ganz-soft.de/klarwin/  
Produktseite: https://ganz-soft.de/klarwin/

## Kacheln

Netzwerk und KI-Last sind **zwei getrennte Kacheln**. Router-Daten über Standard-UPnP/IGD (nicht nur Fritz!Box); ohne UPnP bleiben PC-Netz und LAN-Nachbarn.

Siehe Handbuch-PDF für alle zehn Kacheln und Sicherheitshinweise.
