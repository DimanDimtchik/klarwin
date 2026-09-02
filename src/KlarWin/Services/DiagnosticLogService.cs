using System.IO;
using System.Text;

namespace KlarWin.Services;

public static class DiagnosticLogService
{
    private static readonly object Gate = new();

    public static string LogPath =>
        Path.Combine(DesktopShortcutService.CanonicalInstallDirectory(), "klarwin.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string context, Exception ex) =>
        Write("ERROR", $"{context}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    public static void Write(string level, string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // logging must never crash the app
        }
    }
}
