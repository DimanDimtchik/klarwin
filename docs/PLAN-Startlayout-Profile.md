# Plan: KlarWin-Erweiterung „Startlayout / Arbeitsplatz-Profile“

**Status:** MVP inkl. Opt-in + Phase 5 (KlarWin 1.3.46)  
**Datum:** 2026-09-10  

### Bedienung (1.3.46)

1. Kachel **Autostart** → **Startlayout …**  
2. **Funktion aktivieren** (Opt-in; standardmäßig aus)  
3. **Profil speichern** → **App hinzufügen** → **Layout anwenden**  
4. Optional: **Bei KlarWin-Start anwenden** (setzt Windows-Autostart „KlarWin Startlayout“)  
5. **Profil-Menü …**: aktualisieren, umbenennen, App entfernen, Profil löschen  
6. Haken **Startlayout aktiv** entfernen = Funktion wieder aus (Profile bleiben gespeichert)  
**Herkunft:** Produktidee aus Alltag (Laptop + 2× Ultrawide Büro / 2× 27″ Zuhause)  
**Bezug Chat:** Übergabe auch an [HP LaserJet700](4b4d2d62-5ad7-4fe2-8c88-5252d92e6050) — siehe `HANDOFF-HP-LaserJet700.md`  
**Namensrecht:** siehe `RECHT-Name-KlarWin.md` (Vorabrecherche, keine Rechtsberatung)

## Produktmeinung

Passt zu KlarWin als **begrenzte, alltagsnahe Funktion** — nicht als DisplayFusion-Klon.

| Ja (Scope) | Nein (erstmal out of scope) |
|------------|------------------------------|
| Profile nach Monitoren und/oder Netzwerk | FancyZones, Dauer-Hotkeys, Taskleisten pro Monitor |
| Apps starten + Fenster auf Rollen legen | Vollständiges Fenster-Management 24/7 |
| Anbindung an bestehende Autostart-Kachel | Feature-Parität zu DisplayFusion Pro |

**Kernidee:** Arbeitsplatz erkennen → Startlayout anwenden.  
Nicht „Monitor 1/2/3“ speichern, sondern **Rollen** (`primary`, `left`, `right` …). Hardware wechselt, Absicht bleibt.

## Bestehende KlarWin-Anknüpfung

- `AutostartService` — Einträge listen / ein/aus  
- `NetworkMonitorService` — Netzkontext  
- `KlarWinSettings` — Persistenz  
- `FeatureCatalog.Autostart` — UI-Erweiterung um „Startlayout“

Neu nötig: Display-Inventar, Rollen-Mapping, Fensterplatzierung, Profilspeicher.

## Algorithmus (beliebige Monitor-Setups)

### 1. Kontext erfassen

```
Context = {
  monitors: [{ id, name, manufacturer, serial?, width, height, isPrimary, bounds }],
  network:  { wifiSsid? | gatewayMac? | domain? }
}
```

- Monitor-`id`: EDID (Hersteller+Produkt+Serial), Fallback Name+Auflösung  
- **Nicht** `\\.\DISPLAYn` (instabil)

### 2. Profil wählen (Priorität)

1. Manuell gewähltes Profil  
2. Netzwerk-Match (SSID / Gateway-MAC)  
3. Monitor-Fingerprint (Menge der IDs, Score bei Teilmenge)  
4. Fallback „Nur Laptop“ / letztes Profil  

Beispiel Fingerprints:

- Büro: `{BOE-Laptop, MFG34C5Q, LC34G55T}`  
- Heim: `{BOE-Laptop, Monitor27-A, Monitor27-B}`

### 3. Monitore → Rollen

| Rolle | Büro (Beispiel) | Heim (Beispiel) |
|--------|-----------------|-----------------|
| `primary` | Laptop | Laptop |
| `left` | MiniFire (Namensmatch) | 27″ mit kleinerer X-Position |
| `right` | Samsung LC34… | 27″ rechts |

Reihenfolge: gespeicherte ID → Name/Hersteller → Geometrie (Primary, dann X) → sonst skip/Primary.

### 4. Apps anwenden

Eintrag: `{ name, exe, args?, role, delayMs?, maximize? }`

1. Starten oder bestehenden Prozess nutzen  
2. Hauptfenster abwarten (Timeout + Retry)  
3. In Ziel-`Bounds` verschieben (optional maximieren)  
4. Fehler pro App loggen, Rest fortsetzen  

Beispiel Büro: BlueMail → `primary`, Cursor → `left`, Chrome → `right`.

### 5. UI / Lernen

- „Aktuelles Setup als Profil speichern“  
- Apps Rollen zuweisen  
- „Jetzt anwenden“ + Option „Bei Anmeldung anwenden“  
- Vorschau: Rolle ↔ physischer Monitor  

## Profil-JSON (Skizze)

```json
{
  "name": "Büro",
  "triggers": {
    "networks": ["GatewayMac:…", "SSID:…"],
    "monitorFingerprints": ["BOE09D5", "XEC0030:MFG34C5Q", "SAM7119:LC34G55T"]
  },
  "roles": {
    "primary": { "match": "primary" },
    "left":    { "match": "name:MFG34C5Q" },
    "right":   { "match": "name:LC34G55T" }
  },
  "apps": [
    { "name": "BlueMail", "exe": "…", "role": "primary" },
    { "name": "Cursor",   "exe": "…", "role": "left" },
    { "name": "Chrome",   "exe": "chrome.exe", "role": "right" }
  ]
}
```

## Phasen

1. **Discovery** — Monitore + Netz nur anzeigen ✅  
2. **Manuell anwenden** — Profil bauen, Button „Platzieren“ ✅  
3. **Login-Hook** — KlarWin startet mit Windows, wendet Profil nach kurzer Verzögerung an ✅ (Option „Bei KlarWin-Start anwenden“)  
4. **Auto-Select** — Profilwahl per Netz + Fingerprint ✅  
5. **Feinschliff** — Opt-in, Profil umbenennen/löschen, robustere Fensterplatzierung, KlarWin-Autostart ✅  

### Bedienung (1.3.46)

1. Kachel **Autostart** → **Startlayout …**  
2. **Funktion aktivieren** (Opt-in; standardmäßig aus)  
3. **Profil speichern** → **App hinzufügen** → **Layout anwenden**  
4. Optional: **Bei KlarWin-Start anwenden** (setzt Windows-Autostart „KlarWin Startlayout“)  
5. **Profil-Menü …**: aktualisieren, umbenennen, App entfernen, Profil löschen  
6. Haken **Startlayout aktiv** entfernen = Funktion wieder aus (Profile bleiben gespeichert)  

## Risiken

- Späte Splash-Fenster → Delay/Retry  
- UWP/Store-Apps schwieriger als Win32  
- Mehrere Chrome-Fenster → klare Regel nötig  
- KlarWin muss ggf. kurz mit Windows starten (transparent kommunizieren)

## Abgrenzung Wettbewerb

- **Skript allein:** gut für einen Nutzer, schlecht als Produktfeature  
- **DisplayFusion:** mächtig, teuer, anderer Fokus  
- **KlarWin:** „Arbeitsplatz + Autostart-Layout“ im bestehenden Utility — schlank, erklärbar, skalierbar über Setups
