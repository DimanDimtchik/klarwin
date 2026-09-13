# TODO: Rechtstexte ganz-soft.de ↔ Produkte (Handoff)

**Zweck:** Diese Liste im **CRM-/Website-Chat** lesen und dort die Seiten vorbereiten.  
**KlarWin-Sync erst danach** — hier nur spezifizieren, noch nicht bauen.

**Stand:** 2026-09-13 · Repo: `klarwin` · Live-App: 1.3.58 (eingebettete Texte unter Einstellungen → Rechtliches)

---

## Entscheidung (bereits abgestimmt)

| Dokument | Strategie |
|----------|-----------|
| **Impressum** | **Einmal zentral** (Anbieter Dietrich Ganz / GanzSoft) — alle Produkte verlinken hierher |
| **Datenschutz** | **Gemeinsame Basis** + **Abschnitte/Fragmente pro Produkt** (KlarWin, CRM, …) |
| **Lizenz / AGB** | **Pro Produkt separat** (KlarWin-Freeware ≠ CRM-SaaS) |

Nicht: Impressum dreimal kopieren.

---

## Phase A — CRM / Website (`ganz-soft.de`) — **jetzt**

- [ ] **A1** Zentrale Seite `/impressum` (Firmendaten wie Shop/CRM: Auf dem Bühl 1, Kempten, USt-IdNr., `info@ganz-soft.de`)
- [ ] **A2** Zentrale `/datenschutz` (Website, Hosting, gemeinsame Verantwortlicher-/Rechte-Texte)
- [ ] **A3** Produkt-Fragment oder Unterseite `/datenschutz/klarwin` (lokal, Update-Check, optionale Browser-Erweiterung — Inhalt an `LegalDocuments.Datenschutz` in KlarWin anlehnen)
- [ ] **A4** Produkt-Fragment oder Unterseite `/datenschutz/crm` (Konto, Cloud, AVV-Hinweis, …)
- [ ] **A5** `/lizenz/klarwin` (oder `/klarwin/lizenz`) — Freeware-Nutzungsbedingungen
- [ ] **A6** CRM: AGB / Nutzungsbedingungen unter bestehendem Pfad belassen oder klar verlinken (`/agb` o. Ä.)
- [ ] **A7** Maschinenlesbare Sync-Quelle für Apps festlegen, z. B.:
  - `https://ganz-soft.de/klarwin/legal.json` **oder**
  - Markdown-URLs + Manifest  
  Vorgeschlagenes JSON-Schema:

```json
{
  "version": "1",
  "updated": "2026-09-13",
  "impressum": { "url": "https://ganz-soft.de/impressum.md", "title": "Impressum" },
  "datenschutz": { "url": "https://ganz-soft.de/datenschutz/klarwin.md", "title": "Datenschutz" },
  "lizenz": { "url": "https://ganz-soft.de/lizenz/klarwin.md", "title": "Lizenz" }
}
```

- [ ] **A8** Reine Text-/Markdown-Varianten (ohne Cookie-Banner-HTML), die Desktop-Apps und CRM zuverlässig laden können
- [ ] **A9** Menü/Footer: Impressum · Datenschutz · (pro Produkt Lizenz/AGB)
- [ ] **A10** Kurznotiz in CRM-Repo (z. B. `docs/TODO-Rechtstexte-Sync.md` spiegeln oder verlinken), damit der Chat dort weiterarbeiten kann

**Referenz-Texte in diesem Repo:**  
`src/KlarWin/Services/LegalDocuments.cs` · `docs/RECHT-KlarWin-App.md` · Firmendaten auch in DG `shop/config/legal.php` / `bin/set-company-legal.php`

---

## Phase B — KlarWin (dieses Repo) — **später**, nach A7/A8

- [ ] **B1** Beim Start / Öffnen „Rechtliches“: Manifest + Texte von ganz-soft.de laden
- [ ] **B2** Lokal cachen (`%AppData%\KlarWin\legal\…`) + ETag/`updated` prüfen
- [ ] **B3** Offline-Fallback: weiterhin eingebettete `LegalDocuments` (aktuelle 1.3.58-Texte)
- [ ] **B4** UI: Stand/Datum „von Server“ vs. „Fallback“ anzeigen
- [ ] **B5** Version bumpen, deployen

---

## Phase C — Weitere Produkte (Zukunft)

- [ ] **C1** Pro neuem Produkt: Datenschutz-Fragment + eigene Lizenz/AGB-URL im gleichen Schema
- [ ] **C2** Impressum unverändert zentral lassen

---

## Explizit nicht jetzt

- KlarWin-Code für Sync schreiben
- Cookie-Banner in der Desktop-App
- Deploy nur wegen dieser Planung

---

## Für den anderen Chat (Copy-Paste-Start)

> Bitte `docs/TODO-Rechtstexte-Sync.md` aus dem KlarWin-Repo lesen und **Phase A** auf ganz-soft.de / im CRM umsetzen: zentrales Impressum, Datenschutz Basis + KlarWin/CRM-Abschnitte, Lizenz KlarWin separat, plus `legal.json` (oder Markdown-Manifest) für späteren App-Sync. Phase B bleibt in KlarWin.
