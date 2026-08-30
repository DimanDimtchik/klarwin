# KlarWin

Windows-Werkzeug mit vier Kacheln: Speicher bereinigen, Tempo anheben, Leistung anzeigen, Verknüpfungspfeile ausblenden.

Keine Registry-Wunderkuren. Es werden nur temporäre Dateien, Caches und Einstellungen angefasst, die Windows selbst auch aufräumt.

## Start

```powershell
dotnet run --project src/KlarWin/KlarWin.csproj
```

## Kacheln

1. **Bereinigen** — Benutzer-Temp, Windows-Temp, Delivery-Optimization-Cache, Papierkorb
2. **Beschleunigen** — Energieplan Hochleistung, weniger Animationen, DNS-Flush
3. **Leistung** — CPU, RAM und freier Speicher live
4. **Verknüpfungen** — Overlay-Pfeil per leerem Icon ausblenden (Administrator)

## Hinweise

- Verknüpfungspfeile brauchen Administratorrechte. Nach Windows-Updates kann der Pfeil zurückkommen.
- Visuelle Effekte greifen vollständig nach Abmelden.
- Das Programm löscht keine Dokumente, Fotos oder installierten Programme.
