using System.Diagnostics;
using System.IO;

namespace KlarWin.Services;

/// <summary>
/// Builds human-readable app labels from Windows file metadata (same source as Explorer → Properties → Details).
/// No per-app hardcoding — works for any installed program that embeds version info in its EXE.
/// </summary>
public static class AppFriendlyName
{
    public static string Describe(string entryName, string command)
    {
        var exe = ResolveExecutablePath(command);
        if (!string.IsNullOrWhiteSpace(exe))
        {
            var fromFile = DescribeExecutable(exe);
            if (!string.IsNullOrWhiteSpace(fromFile))
            {
                return fromFile;
            }
        }

        return HumanizeName(entryName);
    }

    public static string DescribeExecutable(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return "";
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            var product = Clean(info.ProductName);
            var description = Clean(info.FileDescription);
            var company = Clean(info.CompanyName);

            if (IsUsefulText(description) && IsUsefulText(product)
                && !description.Equals(product, StringComparison.OrdinalIgnoreCase))
            {
                return $"{product} — {description}";
            }

            if (IsUsefulText(description))
            {
                return description;
            }

            if (IsUsefulText(product) && IsUsefulText(company)
                && !product.Contains(company, StringComparison.OrdinalIgnoreCase))
            {
                return $"{company} — {product}";
            }

            if (IsUsefulText(product))
            {
                return product;
            }

            if (IsUsefulText(company))
            {
                return company;
            }
        }
        catch
        {
            // access denied or invalid path
        }

        return HumanizeName(Path.GetFileNameWithoutExtension(exePath));
    }

    public static string? ResolveExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        command = Environment.ExpandEnvironmentVariables(command.Trim().Trim('"'));

        if (command.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(command))
        {
            var target = ReadShortcutTarget(command);
            if (!string.IsNullOrWhiteSpace(target))
            {
                command = target;
            }
        }

        var exe = ExtractExecutableToken(command);
        if (string.IsNullOrWhiteSpace(exe))
        {
            return null;
        }

        exe = Environment.ExpandEnvironmentVariables(exe);
        if (File.Exists(exe))
        {
            return Path.GetFullPath(exe);
        }

        var fileName = Path.GetFileName(exe);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        if (!fileName.Contains('.', StringComparison.Ordinal))
        {
            fileName += ".exe";
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch
            {
                // invalid PATH entry
            }
        }

        // Kein rekursives Durchsuchen von Program Files — pro Autostart-Eintrag oft Sekunden.
        return null;
    }

    private static string? ReadShortcutTarget(string lnkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            var shell = Activator.CreateInstance(shellType);
            if (shell is null) return null;
            dynamic shortcut = shell.GetType().InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                [lnkPath])!;
            return shortcut.TargetPath as string;
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractExecutableToken(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : command.Trim('"');
        }

        var space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    private static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return text.Replace("®", "").Replace("™", "").Trim();
    }

    private static bool IsUsefulText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 3) return false;
        var lower = text.ToLowerInvariant();
        string[] junk =
        [
            "application", "applauncher", "program starter", "setup", "installer",
            "host process", "runtime broker", "conhost", "cmd", "launcher"
        ];
        return junk.All(j => !lower.Equals(j, StringComparison.OrdinalIgnoreCase));
    }

    private static string HumanizeName(string name)
    {
        name = name.Replace('_', ' ').Replace('-', ' ').Trim();
        return string.IsNullOrWhiteSpace(name) ? "Unbekanntes Programm" : name;
    }
}
