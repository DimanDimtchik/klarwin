using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace KlarWin.Services;

public sealed class TransferFolderTarget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public static class TransferFolderService
{
    private static string PinRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KlarWin", "TransferPins");

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

    /// <summary>
    /// Pins a junction named like the KlarWin label (not the real folder name),
    /// so Quick Access shows e.g. „IQ Personal“ instead of „Personal“ / parent name.
    /// Does not pin subfolders — those in Quick Access come from Windows „häufig verwendet“.
    /// </summary>
    public static (bool Ok, string Message) PinToQuickAccess(string path, string displayName)
    {
        if (!DirectoryExists(path))
            return (false, "Ordner nicht gefunden.");

        var label = string.IsNullOrWhiteSpace(displayName)
            ? SuggestNameFromPath(path)
            : displayName.Trim();

        try
        {
            var pinPath = EnsureNamedPin(path, label);
            var pinResult = InvokePinToHome(pinPath);
            if (!pinResult.Ok)
                return pinResult;

            return (true,
                $"„{label}“ im Explorer-Schnellzugriff angeheftet (KlarWin-Name).\n\n" +
                "Unterordner heftet KlarWin nicht an. Einträge wie Lohnabrechnungen/MA11 stammen von Windows („häufig verwendet“) — die können Sie im Explorer per Rechtsklick „Von Schnellzugriff lösen“ entfernen.");
        }
        catch (Exception ex)
        {
            return (false, UserFriendlyErrors.Translate(ex));
        }
    }

    private static string EnsureNamedPin(string targetPath, string displayName)
    {
        Directory.CreateDirectory(PinRoot);
        var fullTarget = Path.GetFullPath(targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var pinPath = Path.Combine(PinRoot, SanitizeFileName(displayName));

        if (Directory.Exists(pinPath))
        {
            var existing = TryResolveReparseTarget(pinPath);
            if (!string.IsNullOrEmpty(existing)
                && existing.Equals(fullTarget, StringComparison.OrdinalIgnoreCase))
                return pinPath;

            // Replace wrong/old pin link (deletes junction only, not the real folder)
            Directory.Delete(pinPath);
        }
        else if (File.Exists(pinPath))
        {
            File.Delete(pinPath);
        }

        CreateJunction(pinPath, fullTarget);
        return pinPath;
    }

    private static void CreateJunction(string junctionPath, string targetPath)
    {
        // Directory junction needs no admin; works for local and many synced folders.
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("mklink konnte nicht gestartet werden.");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(15000);
        if (proc.ExitCode != 0 || !Directory.Exists(junctionPath))
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
    }

    private static string? TryResolveReparseTarget(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return Path.GetFullPath(path);

            // Follow the junction once via GetFinalPathNameByHandle-less approach:
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static (bool Ok, string Message) InvokePinToHome(string path)
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

        return (true, "ok");
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

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            if (Array.IndexOf(invalid, ch) >= 0) sb.Append('-');
            else sb.Append(ch);
        }

        var cleaned = sb.ToString().Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(cleaned) ? "Zielordner" : cleaned;
    }
}
