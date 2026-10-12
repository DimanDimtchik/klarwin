using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace KlarWin.Services;

public sealed class AutostartEntry
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string Location { get; init; } = "";
    public bool Enabled { get; init; } = true;
}

public sealed class AutostartService
{
    public IReadOnlyList<AutostartEntry> ListEntries()
    {
        var list = new List<AutostartEntry>();
        AddRegistry(list, Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Aktueller Benutzer");
        AddRegistry(list, Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "Dieser PC");
        AddStartupFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Autostart-Ordner (Benutzer)");
        AddStartupFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Autostart-Ordner (Alle)");
        return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public string Disable(AutostartEntry entry)
    {
        if (entry.Location.Contains("Ordner", StringComparison.Ordinal))
        {
            if (File.Exists(entry.Command))
            {
                var disabled = entry.Command + ".disabled";
                File.Move(entry.Command, disabled, overwrite: true);
                return $"{entry.Name} deaktiviert.";
            }
            return "Datei nicht gefunden.";
        }

        var hive = entry.Location.Contains("Benutzer", StringComparison.Ordinal) ? Registry.CurrentUser : Registry.LocalMachine;
        using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (key is null)
        {
            return "Registrierung nicht erreichbar. Als Administrator starten.";
        }

        key.DeleteValue(entry.Name, false);
        return $"{entry.Name} aus dem Autostart entfernt.";
    }

    public void OpenTaskManagerStartup()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "taskmgr.exe",
            Arguments = "/7",
            UseShellExecute = true
        });
    }

    public string AddToAutostart(AutostartCandidate candidate)
    {
        if (!File.Exists(candidate.ExecutablePath))
        {
            return "Programm wurde nicht gefunden.";
        }

        var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        Directory.CreateDirectory(startup);
        var linkName = SanitizeFileName(candidate.Name) + ".lnk";
        var linkPath = Path.Combine(startup, linkName);
        if (File.Exists(linkPath))
        {
            return $"„{candidate.Name}“ startet bereits über den Autostart-Ordner.";
        }

        try
        {
            CreateShortcut(linkPath, candidate.ExecutablePath, candidate.Name);
            return $"„{candidate.Name}“ wurde zum Autostart hinzugefügt (Verknüpfung im Benutzer-Autostartordner).";
        }
        catch (Exception ex)
        {
            return $"Konnte nicht hinzufügen: {ex.Message}";
        }
    }

    public static string? ExtractExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 1)
            {
                return command[1..end];
            }
        }

        var space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name.Length > 80 ? name[..80] : name;
    }

    private static void CreateShortcut(string linkPath, string targetExe, string description)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Verknüpfungen sind auf diesem System nicht verfügbar.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(linkPath);
        link.TargetPath = targetExe;
        link.WorkingDirectory = Path.GetDirectoryName(targetExe) ?? "";
        link.Description = description;
        link.Save();
    }

    private static void AddRegistry(List<AutostartEntry> list, RegistryKey root, string path, string location)
    {
        try
        {
            using var key = root.OpenSubKey(path, false);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                var value = key.GetValue(name)?.ToString() ?? "";
                list.Add(new AutostartEntry { Name = name, Command = value, Location = location });
            }
        }
        catch
        {
            // access denied for HKLM without admin
        }
    }

    private static void AddStartupFolder(List<AutostartEntry> list, string folder, string location)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            list.Add(new AutostartEntry
            {
                Name = Path.GetFileNameWithoutExtension(file),
                Command = file,
                Location = location
            });
        }
    }
}
