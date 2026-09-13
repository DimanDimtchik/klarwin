using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace KlarWin.Services;

public sealed class TransferFolderTarget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Path template; may contain placeholders like {yyyy}.</summary>
    public string Path { get; set; } = "";
}

public static class TransferFolderService
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static IReadOnlyList<(string Token, string Example, string Hint)> Placeholders { get; } =
    [
        ("{yyyy}", DateTime.Now.ToString("yyyy"), "Jahr (vierstellig) — wechselt am 1.1."),
        ("{jahr}", DateTime.Now.ToString("yyyy"), "Jahr (deutscher Alias)"),
        ("{yy}", DateTime.Now.ToString("yy"), "Jahr (zweistellig)"),
        ("{MM}", DateTime.Now.ToString("MM"), "Monat 01–12"),
        ("{monat}", DateTime.Now.ToString("MM"), "Monat (Alias)"),
        ("{MMMM}", DateTime.Now.ToString("MMMM", German), "Monatsname (deutsch)"),
        ("{dd}", DateTime.Now.ToString("dd"), "Tag 01–31"),
        ("{yyyy-MM}", DateTime.Now.ToString("yyyy-MM"), "Jahr-Monat, z. B. 2026-09"),
        ("{yyyyMM}", DateTime.Now.ToString("yyyyMM"), "JahrMonat kompakt"),
        ("{Q}", (((DateTime.Now.Month - 1) / 3) + 1).ToString(), "Quartal 1–4"),
        ("{quartal}", (((DateTime.Now.Month - 1) / 3) + 1).ToString(), "Quartal (Alias)"),
        ("{WW}", ISOWeek.GetWeekOfYear(DateTime.Now).ToString("00"), "Kalenderwoche (ISO)"),
        ("{kw}", ISOWeek.GetWeekOfYear(DateTime.Now).ToString("00"), "Kalenderwoche (Alias)"),
        ("{user}", Environment.UserName, "Windows-Benutzername"),
        ("{documents}", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Dokumente-Ordner"),
        ("{desktop}", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Desktop"),
        ("{downloads}", GetDownloadsFolder(), "Downloads-Ordner")
    ];

    public static string PlaceholdersHelpText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Platzhalter (beim Öffnen/Verschieben aufgelöst):");
        foreach (var (token, example, hint) in Placeholders)
            sb.AppendLine($"  {token}  →  {example}  ({hint})");
        sb.AppendLine();
        sb.AppendLine("Beispiel: …\\Belege\\{yyyy}  →  …\\Belege\\2026 (ab 01.01.2027 automatisch 2027)");
        return sb.ToString();
    }

    /// <summary>Replace path placeholders with values for <paramref name="when"/> (default: now).</summary>
    public static string ResolvePath(string? template, DateTime? when = null)
    {
        if (string.IsNullOrWhiteSpace(template)) return "";
        var t = when ?? DateTime.Now;
        var path = template.Trim();

        path = ReplaceIgnoreCase(path, "{yyyy}", t.ToString("yyyy"));
        path = ReplaceIgnoreCase(path, "{jahr}", t.ToString("yyyy"));
        path = ReplaceIgnoreCase(path, "{yy}", t.ToString("yy"));
        path = ReplaceIgnoreCase(path, "{MM}", t.ToString("MM"));
        path = ReplaceIgnoreCase(path, "{monat}", t.ToString("MM"));
        path = ReplaceIgnoreCase(path, "{MMMM}", t.ToString("MMMM", German));
        path = ReplaceIgnoreCase(path, "{dd}", t.ToString("dd"));
        path = ReplaceIgnoreCase(path, "{tag}", t.ToString("dd"));
        path = ReplaceIgnoreCase(path, "{yyyy-MM}", t.ToString("yyyy-MM"));
        path = ReplaceIgnoreCase(path, "{yyyyMM}", t.ToString("yyyyMM"));
        var q = ((t.Month - 1) / 3) + 1;
        path = ReplaceIgnoreCase(path, "{Q}", q.ToString());
        path = ReplaceIgnoreCase(path, "{quartal}", q.ToString());
        var week = ISOWeek.GetWeekOfYear(t).ToString("00");
        path = ReplaceIgnoreCase(path, "{WW}", week);
        path = ReplaceIgnoreCase(path, "{kw}", week);
        path = ReplaceIgnoreCase(path, "{user}", Environment.UserName);
        path = ReplaceIgnoreCase(path, "{documents}", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        path = ReplaceIgnoreCase(path, "{desktop}", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        path = ReplaceIgnoreCase(path, "{downloads}", GetDownloadsFolder());

        try
        {
            path = Environment.ExpandEnvironmentVariables(path);
            if (path.Contains('{')) return path;
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    public static bool ContainsPlaceholders(string? path) =>
        !string.IsNullOrEmpty(path) && path.Contains('{') && path.Contains('}');

    private static string PinRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KlarWin", "TransferPins");

    public static bool DirectoryExists(string? path)
    {
        try
        {
            var resolved = ResolvePath(path);
            if (string.IsNullOrWhiteSpace(resolved)) return false;
            if (Directory.Exists(resolved)) return true;
            // Template like …\{yyyy}: parent may exist before the year folder is created.
            if (ContainsPlaceholders(path))
            {
                var parent = Path.GetDirectoryName(resolved);
                return !string.IsNullOrEmpty(parent) && Directory.Exists(parent);
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public static string StatusLabel(TransferFolderTarget target)
    {
        var resolved = ResolvePath(target.Path);
        if (string.IsNullOrWhiteSpace(resolved)) return "Pfad fehlt";
        if (Directory.Exists(resolved)) return "Bereit";
        if (ContainsPlaceholders(target.Path))
        {
            var parent = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                return "wird angelegt";
            return "Ordner fehlt";
        }

        return "Ordner fehlt";
    }

    /// <summary>Normalize for storage: keep templates with {…}, otherwise full path.</summary>
    public static string NormalizeStoredPath(string path)
    {
        path = path.Trim();
        if (ContainsPlaceholders(path)) return path;
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    public static (bool Ok, string Message) OpenInExplorer(string pathTemplate)
    {
        var path = ResolvePath(pathTemplate);
        if (string.IsNullOrWhiteSpace(path))
            return (false, "Pfad ist leer.");

        if (!Directory.Exists(path))
        {
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                return (false, "Ordner nicht gefunden und konnte nicht angelegt werden:\n" + UserFriendlyErrors.Translate(ex));
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
            var note = ContainsPlaceholders(pathTemplate) ? $"\n(aus Vorlage: {pathTemplate})" : "";
            return (true, $"Geöffnet: {path}{note}");
        }
        catch (Exception ex)
        {
            return (false, UserFriendlyErrors.Translate(ex));
        }
    }

    public static (bool Ok, string Message) PinToQuickAccess(string pathTemplate, string displayName)
    {
        var path = ResolvePath(pathTemplate);
        if (!Directory.Exists(path))
        {
            try { Directory.CreateDirectory(path); }
            catch { return (false, "Ordner nicht gefunden."); }
        }

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
                $"„{label}“ im Explorer-Schnellzugriff angeheftet → {path}\n\n" +
                "Unterordner heftet KlarWin nicht an. „Häufig verwendet“-Einträge kommen von Windows.");
        }
        catch (Exception ex)
        {
            return (false, UserFriendlyErrors.Translate(ex));
        }
    }

    private static string ReplaceIgnoreCase(string input, string token, string value) =>
        Regex.Replace(input, Regex.Escape(token), value.Replace("$", "$$"), RegexOptions.IgnoreCase);

    private static string GetDownloadsFolder()
    {
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var downloads = Path.Combine(userProfile, "Downloads");
            if (Directory.Exists(downloads)) return downloads;
        }
        catch { /* ignore */ }

        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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
            var resolved = ResolvePath(path);
            var name = Path.GetFileName(resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
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
