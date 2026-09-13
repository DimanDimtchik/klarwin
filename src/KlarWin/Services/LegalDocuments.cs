namespace KlarWin.Services;

/// <summary>Impressum, Datenschutz und Lizenz (Anzeige in Einstellungen).</summary>
public static class LegalDocuments
{
    public const string Stand = "12.09.2026";

    public static string Impressum { get; } = """
        Impressum

        Angaben gemäß § 5 DDG (früher TMG)

        Anbieter
        Dietrich Ganz
        Einzelunternehmen (Marke: GanzSoft / KlarWin)
        Auf dem Bühl 1
        87437 Kempten (Allgäu)
        Deutschland

        Kontakt
        E-Mail: info@ganz-soft.de
        Web: https://ganz-soft.de
        Produktseite KlarWin: https://ganz-soft.de/klarwin

        Steuerliche Angaben
        USt-IdNr.: DE461693381
        W-IdNr.: DE461693381-00001
        Steuernummer: 127/219/40770

        Verantwortlich für den Inhalt
        Dietrich Ganz
        (Anschrift wie oben)

        Hosting der Download-/Update-Server
        ALL-INKL.COM – Neue Medien Münnich
        Hauptstraße 68, 02742 Friedersdorf

        Haftung für Inhalte und Links
        Als Anbieter sind wir für eigene Inhalte nach den allgemeinen Gesetzen verantwortlich.
        Für Inhalte verlinkter fremder Seiten übernehmen wir keine Gewähr. Bei Bekanntwerden
        von Rechtsverletzungen entfernen wir entsprechende Inhalte bzw. Links umgehend.

        Hinweis
        Dies ist ein Informationsangebot in der App. Maßgeblich kann zusätzlich das
        Impressum auf https://ganz-soft.de sein, sobald dort veröffentlicht.
        """;

    public static string Datenschutz { get; } = """
        Datenschutzerklärung für KlarWin

        Stand: 12.09.2026
        Verantwortlicher: Dietrich Ganz, Auf dem Bühl 1, 87437 Kempten (Allgäu),
        E-Mail: info@ganz-soft.de (siehe Impressum)

        1. Kurzüberblick
        KlarWin ist eine lokale Windows-Anwendung. Die meisten Funktionen laufen nur auf
        Ihrem PC. Es werden keine Nutzerkonten geführt und keine Werbe-/Analyse-Cookies
        in der App gesetzt.

        2. Daten, die lokal auf Ihrem Gerät verarbeitet werden
        Je nach Nutzung speichert KlarWin Einstellungen und Arbeitsdaten lokal, z. B.:
        • Darstellung (Farben, Schriften, Desktop-Symbol)
        • Up-/Download-Zielordner und optionale Browser-Download-Regeln
        • Startlayout-Profile (optional)
        • Notizen zu Netzgeräten / gelernte Herstellerkennungen (optional)
        • Sicherungen von Autostart-Einträgen (optional)

        Diese Daten verlassen Ihren PC nicht über KlarWin, außer Sie exportieren oder
        kopieren sie selbst.

        3. Netzwerkanfragen der App
        KlarWin kann aus technischen Gründen kurze HTTPS-Anfragen stellen, z. B.:
        • Update-Prüfung und Download von version.json sowie KlarWin-Setup.zip
          (Server von GanzSoft / Spiegel)
        • Abruf öffentlicher Herstellerlisten zu MAC-Adressen (OUI), sofern die
          Netzwerkkachel genutzt wird
        • Downloads von Hilfsprogrammen/ISOs, die Sie ausdrücklich starten
          (z. B. Windows-ISO über das gebündelte Fido-Skript bei Microsoft)

        Dabei können technisch übliche Verbindungsdaten beim jeweiligen Server anfallen
        (IP-Adresse, Zeitpunkt, User-Agent). Wir nutzen Update-Aufrufe nicht zu Tracking
        oder Werbung.

        4. Optionale Browser-Erweiterung
        Wenn Sie die KlarWin-Download-Erweiterung installieren und Regeln anlegen,
        verarbeitet KlarWin Download-Metadaten (z. B. Dateipfad, Website-Host), um Dateien
        in Ihre Zielordner zu verschieben. Die Erweiterung kommuniziert lokal über
        Native Messaging mit KlarWin. Ohne Installation der Erweiterung entfällt diese
        Verarbeitung.

        5. Keine Weitergabe zu Werbezwecken
        Wir verkaufen oder vermieten keine personenbezogenen Daten. Eine Weitergabe
        erfolgt nur, soweit gesetzlich erforderlich oder für den Betrieb der von Ihnen
        ausgelösten Downloads/Updates technisch nötig (z. B. Hosting-Anbieter).

        6. Speicherdauer
        Lokale Einstellungen bleiben, bis Sie sie löschen oder KlarWin deinstallieren
        (typische Pfade unter %AppData% / %LocalAppData%\KlarWin). Server-Logdateien
        beim Hoster unterliegen dessen üblichen Aufbewahrungsfristen.

        7. Ihre Rechte (DSGVO)
        Soweit die DSGVO greift, haben Sie u. a. Rechte auf Auskunft, Berichtigung,
        Löschung, Einschränkung, Datenübertragbarkeit und Widerspruch sowie das Recht,
        sich bei einer Aufsichtsbehörde zu beschweren.
        Für rein lokale Daten auf Ihrem PC können Sie diese selbst löschen; bei Fragen
        zu Server-Logs der Update-Server: info@ganz-soft.de

        8. Kinder
        KlarWin richtet sich nicht gezielt an Kinder unter 16 Jahren.

        9. Änderungen
        Wir können diese Erklärung anpassen, wenn sich Funktionen oder Rechtslage ändern.
        Die aktuelle Fassung finden Sie in den Einstellungen der installierten Version.

        Hinweis: Dies ist eine produktspezifische Erklärung für KlarWin und ersetzt
        nicht die allgemeine Datenschutzerklärung der Website ganz-soft.de, sobald dort
        eine veröffentlicht ist.
        """;

    public static string Lizenz { get; } = """
        Lizenzbedingungen – KlarWin

        Copyright © 2026 Dietrich Ganz / GanzSoft
        Stand: 12.09.2026

        1. Nutzungsrecht
        KlarWin darf kostenlos für private und betriebliche Zwecke auf Windows-PCs
        installiert und genutzt werden. Der Quellcode und die Marken „KlarWin“ und
        „GanzSoft“ verbleiben beim Rechteinhaber, soweit nicht anders gekennzeichnet.

        2. Weitergabe
        Die Weitergabe der originalen Installationsdateien von ganz-soft.de (unverändert)
        ist erlaubt. Das Verändern, Entpacken und erneute Veröffentlichen unter eigenem
        Namen, das Entfernen von Hinweisen auf GanzSoft oder das Anbieten als eigenes
        Produkt ohne schriftliche Erlaubnis ist nicht gestattet.

        3. Gewährleistung und Haftung
        KlarWin wird „wie besehen“ bereitgestellt. Soweit gesetzlich zulässig,
        übernehmen wir keine Gewähr für Fehlerfreiheit, Eignung für einen bestimmten
        Zweck oder ununterbrochenen Betrieb. Für Schäden aus der Nutzung haftet der
        Anbieter nur bei Vorsatz und grober Fahrlässigkeit sowie nach zwingendem Recht
        (z. B. Produkthaftung, Verletzung von Leben, Körper, Gesundheit).

        4. Sensible Funktionen
        Funktionen wie Aufräumen, Autostart, Netzwerkinformationen, Startlayout oder
        Browser-Integration greifen in System und Dateien ein. Nutzen Sie Sicherungen
        und prüfen Sie Aktionen, bevor Sie bestätigen. Sie handeln auf eigene
        Verantwortung im Rahmen der App-Hinweise.

        5. Drittsoftware (Auszug)
        KlarWin enthält bzw. nutzt fremde Komponenten mit eigenen Lizenzen, u. a.:
        • Fido (Pete Batard) – GPL-3.0 – Tools/Fido.ps1
          https://github.com/pbatard/Fido
        • LibreHardwareMonitorLib – eigene Lizenz der jeweiligen Autoren
        • .NET / Windows-Komponenten – Microsoft-Lizenzbedingungen

        Für diese Komponenten gelten die jeweiligen Lizenztexte; KlarWin selbst
        unterliegt den Bedingungen dieses Abschnitts.

        6. Updates
        Updates können Funktions- und Lizenztexte ändern. Mit der Installation einer
        neueren Version gelten die darin enthaltenen Texte.

        7. Kontakt
        Fragen zu Lizenz und Nutzung: info@ganz-soft.de
        """;

    public static IReadOnlyList<(string Id, string Title, string Body)> All { get; } =
    [
        ("impressum", "Impressum", Impressum),
        ("datenschutz", "Datenschutz", Datenschutz),
        ("lizenz", "Lizenz", Lizenz)
    ];
}
