namespace KlarWin.Services;

public static class UserFriendlyErrors
{
    public static string Translate(Exception ex)
    {
        var msg = ex.Message;
        if (msg.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("weil sie von einem anderen Prozess verwendet wird", StringComparison.OrdinalIgnoreCase))
        {
            return "Die Datei wird gerade von einem anderen Programm verwendet (z. B. Explorer, Browser oder Download). " +
                   "Datei schließen, ZIP-Ordner schließen und erneut versuchen.";
        }

        if (msg.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Zugriff verweigert", StringComparison.OrdinalIgnoreCase))
        {
            return "Zugriff verweigert. KlarWin ggf. als Administrator starten oder eine andere Datei wählen.";
        }

        if (msg.Contains("SSL connection could not be established", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("The remote certificate is invalid", StringComparison.OrdinalIgnoreCase))
        {
            return "Sichere Verbindung zum Update-Server fehlgeschlagen (SSL). " +
                   "KlarWin versucht alternative Quellen — oder Setup manuell laden: https://ganz-soft.de/klarwin/KlarWin-Setup.zip";
        }

        if (msg.Contains("Could not find", StringComparison.OrdinalIgnoreCase))
        {
            return "Datei oder Ordner wurde nicht gefunden.";
        }

        return string.IsNullOrWhiteSpace(msg) ? "Unbekannter Fehler." : msg;
    }
}
