namespace KlarWin.Services;

public sealed record CatalogFeature(string Label, bool RequiresAdmin);

/// <summary>All KlarWin features shown on the main tile page — with admin requirement flags.</summary>
public static class FeatureCatalog
{
    public static IReadOnlyList<CatalogFeature> Clean { get; } =
    [
        new("Benutzer-Temp", false),
        new("Windows-Temp", true),
        new("Download-Cache", true),
        new("Papierkorb leeren", false),
        new("Download-Ordner", false)
    ];

    public static IReadOnlyList<CatalogFeature> Speed { get; } =
    [
        new("Energieplan", true),
        new("Akku-Lademodus", false),
        new("Kühlprofil", false),
        new("Bildschirm-Timeout", true),
        new("DNS-Cache leeren", false),
        new("Animationen reduzieren", false)
    ];

    public static IReadOnlyList<CatalogFeature> Performance { get; } =
    [
        new("CPU-Auslastung", false),
        new("RAM-Auslastung", false),
        new("Festplatten-Belegung", false),
        new("Temperatur (CPU/Thermal)", false),
        new("System-Gesundheit", false)
    ];

    public static IReadOnlyList<CatalogFeature> Overlay { get; } =
    [
        new("Verknüpfungspfeile ausblenden", true),
        new("Pfeile wiederherstellen", true)
    ];

    public static IReadOnlyList<CatalogFeature> Autostart { get; } =
    [
        new("Autostart-Einträge anzeigen", false),
        new("Programme abmelden (HKLM)", true)
    ];

    public static IReadOnlyList<CatalogFeature> Wipe { get; } =
    [
        new("Datei sicher löschen", false),
        new("Freien Speicher überschreiben", true)
    ];

    public static IReadOnlyList<CatalogFeature> Recover { get; } =
    [
        new("Papierkorb wiederherstellen", false),
        new("NTFS-Protokoll durchsuchen", true)
    ];

    public static IReadOnlyList<CatalogFeature> Keys { get; } =
    [
        new("Windows-/Office-Keys auslesen", false),
        new("Als Textdatei exportieren", false)
    ];

    public static IReadOnlyList<CatalogFeature> Network { get; } =
    [
        new("Geräte im Netzwerk", false),
        new("Geschwindigkeit & Ping", false),
        new("Geräte-Bemerkungen", false)
    ];

    public static IReadOnlyList<CatalogFeature> AiLoad { get; } =
    [
        new("KI-/GPU-Prozesse", false),
        new("CPU-, RAM-, GPU-Last", false)
    ];

    public static IReadOnlyList<CatalogFeature> Updates { get; } =
    [
        new("Programme suchen (winget)", false),
        new("Treiber & Apps installieren", true),
        new("Windows Update", false),
        new("KlarWin-Update (Einstellungen)", false)
    ];

    public static IReadOnlyList<CatalogFeature> Backup { get; } =
    [
        new("Wiederherstellungspunkt erstellen", true),
        new("Passende Windows-ISO (automatisch)", false),
        new("Systemschutz-Einstellungen", false)
    ];

    public static IReadOnlyList<CatalogFeature> UsbStick { get; } =
    [
        new("USB-Stick für Windows formatieren", true),
        new("NTFS · exFAT · FAT32", false),
        new("Linux/ext4-Stick unter Windows nutzbar machen", false),
        new("Windows-ISO automatisch laden", false)
    ];
}
