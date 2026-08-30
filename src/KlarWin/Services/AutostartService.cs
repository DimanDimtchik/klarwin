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
