using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace KlarWin.Services;

public sealed class TransferFolderTarget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public static class TransferFolderService
{
    public static bool DirectoryExists(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    public static string StatusLabel(TransferFolderTarget target) =>
        DirectoryExists(target.Path) ? "Bereit" : "Ordner fehlt";

    public static (bool Ok, string Message) OpenInExplorer(string path)
    {
        if (!DirectoryExists(path))
            return (false, "Ordner nicht gefunden. Bitte Ziel bearbeiten oder neu anlegen.");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
            return (true, $"Geöffnet: {path}");
        }
        catch (Exception ex)
        {
            return (false, UserFriendlyErrors.Translate(ex));
        }
    }

    /// <summary>Pin folder to Windows Explorer Quick Access (Schnellzugriff).</summary>
    public static (bool Ok, string Message) PinToQuickAccess(string path)
    {
        if (!DirectoryExists(path))
            return (false, "Ordner nicht gefunden.");

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
                return (false, "Schnellzugriff konnte nicht angesprochen werden.");

            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic folder = shell.NameSpace(path);
                if (folder is null)
                    return (false, "Ordner für Schnellzugriff nicht erreichbar.");

                try
                {
                    dynamic self = folder.Self;
                    try
                    {
                        self.InvokeVerb("pintohome");
                    }
                    finally
                    {
                        Marshal.FinalReleaseComObject(self);
                    }
                }
                finally
                {
                    Marshal.FinalReleaseComObject(folder);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }

            return (true, "Im Explorer-Schnellzugriff angeheftet.");
        }
        catch (Exception ex)
        {
            return (false, UserFriendlyErrors.Translate(ex));
        }
    }

    public static string SuggestNameFromPath(string path)
    {
        try
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(name) ? "Zielordner" : name;
        }
        catch
        {
            return "Zielordner";
        }
    }
}
