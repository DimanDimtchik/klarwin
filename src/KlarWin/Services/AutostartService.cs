using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace KlarWin.Services;

public sealed class AutostartEntry
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string Location { get; init; } = "";
    public string Description { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string Hive { get; init; } = "";
    public string IconSource { get; init; } = "";
}

public sealed class AutostartService
{
    private readonly KlarWinSettings _settings;

    public AutostartService(KlarWinSettings settings)
    {
        _settings = settings;
    }

    public IReadOnlyList<AutostartEntry> ListEntries()
    {
        var list = new List<AutostartEntry>();
        AddRegistry(list, Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Aktueller Benutzer", "HKCU");
        AddRegistry(list, Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "Dieser PC (alle Benutzer)", "HKLM");
        AddStartupFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Autostart-Ordner (Benutzer)", "FolderUser");
        AddStartupFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Autostart-Ordner (Alle)", "FolderAll");
        AddDisabledFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup));
        AddDisabledFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));
        return Deduplicate(list).OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public string Disable(AutostartEntry entry)
    {
        if (entry.Location.Contains("Ordner", StringComparison.Ordinal))
        {
            if (File.Exists(entry.Command))
            {
                var disabled = entry.Command + ".disabled";
                File.Move(entry.Command, disabled, overwrite: true);
                return $"{entry.Name} deaktiviert (Datei umbenannt).";
            }
            return "Datei nicht gefunden.";
        }

        var hive = entry.Hive == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
        using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (key is null)
        {
            return "Registrierung nicht erreichbar. Als Administrator starten.";
        }

        var command = key.GetValue(entry.Name)?.ToString() ?? entry.Command;
        _settings.AutostartBackup[entry.Name] = new AutostartBackupEntry
        {
            Name = entry.Name,
            Command = command,
            Location = entry.Location,
            Hive = entry.Hive
        };
        _settings.Save();
        key.DeleteValue(entry.Name, false);
        return $"{entry.Name} aus dem Autostart entfernt (Sicherung in KlarWin-Einstellungen).";
    }

    public string Enable(AutostartEntry entry)
    {
        if (entry.Command.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) && File.Exists(entry.Command))
        {
            var enabled = entry.Command[..^".disabled".Length];
            File.Move(entry.Command, enabled, overwrite: true);
            return $"{entry.Name} wieder aktiviert.";
        }

        if (_settings.AutostartBackup.TryGetValue(entry.Name, out var backup))
        {
            var hive = backup.Hive == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
            using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)
                ?? hive.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            key.SetValue(backup.Name, backup.Command);
            _settings.AutostartBackup.Remove(entry.Name);
            _settings.Save();
            return $"{entry.Name} wieder im Autostart eingetragen.";
        }

        return "Keine Sicherung gefunden — manuell im Task-Manager oder Ordner hinzufügen.";
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

    private static IReadOnlyList<AutostartEntry> Deduplicate(List<AutostartEntry> list)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AutostartEntry>();
        foreach (var entry in list)
        {
            var key = BuildDedupeKey(entry);
            if (!seen.Add(key)) continue;
            result.Add(entry);
        }

        return result;
    }

    private static string BuildDedupeKey(AutostartEntry entry)
    {
        var exe = AppIconHelper.ResolveExecutable(entry.Command);
        if (!string.IsNullOrWhiteSpace(exe))
        {
            return Path.GetFullPath(exe).ToLowerInvariant();
        }

        return $"{entry.Name}|{entry.Command}|{entry.Location}".ToLowerInvariant();
    }

    private static void AddRegistry(List<AutostartEntry> list, RegistryKey root, string path, string location, string hive)
    {
        try
        {
            using var key = root.OpenSubKey(path, false);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                var value = key.GetValue(name)?.ToString() ?? "";
                list.Add(new AutostartEntry
                {
                    Name = name,
                    Command = value,
                    Location = location,
                    Hive = hive,
                    Description = AppFriendlyName.Describe(name, value),
                    IconSource = value,
                    Enabled = true
                });
            }
        }
        catch
        {
            // access denied for HKLM without admin
        }
    }

    private static void AddStartupFolder(List<AutostartEntry> list, string folder, string location, string hive)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileNameWithoutExtension(file);
            list.Add(new AutostartEntry
            {
                Name = name,
                Command = file,
                Location = location,
                Hive = hive,
                Description = AppFriendlyName.Describe(name, file),
                IconSource = file,
                Enabled = true
            });
        }
    }

    private static void AddDisabledFolder(List<AutostartEntry> list, string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder, "*.disabled"))
        {
            list.Add(new AutostartEntry
            {
                Name = Path.GetFileNameWithoutExtension(file),
                Command = file,
                Location = "Deaktiviert (Ordner)",
                Hive = "FolderUser",
                Description = AppFriendlyName.Describe(Path.GetFileNameWithoutExtension(file), file),
                IconSource = file,
                Enabled = false
            });
        }
    }
}
