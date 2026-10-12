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
    public bool IsRegistry { get; init; }
    public bool IsLocalMachine { get; init; }
    /// <summary>Registry / StartupApproved value name (may differ from display Name for .lnk).</summary>
    public string ApprovalKey { get; init; } = "";
}

public sealed class AutostartService
{
    private const string DisabledRunKey = @"Software\GanzSoft\KlarWin\DisabledRun";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    public IReadOnlyList<AutostartEntry> ListEntries()
    {
        var list = new List<AutostartEntry>();
        AddRegistry(list, Registry.CurrentUser, RunKeyPath, "Aktueller Benutzer", false);
        AddRegistry(list, Registry.LocalMachine, RunKeyPath, "Dieser PC (alle Benutzer)", true);
        AddStartupFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Autostart-Ordner (Benutzer)");
        AddStartupFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Autostart-Ordner (Alle)");
        AddDisabledRegistryBackups(list);
        return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public string Disable(AutostartEntry entry)
    {
        if (!entry.Enabled)
            return $"„{entry.Name}“ ist bereits inaktiv.";

        if (entry.IsRegistry)
        {
            // Prefer Windows StartupApproved (Task Manager style) when possible
            if (TrySetStartupApproved(entry, enabled: false))
                return $"{entry.Name} deaktiviert (wie in den Windows-Einstellungen).";
            return DisableRegistryEntry(entry);
        }

        if (entry.Location.Contains("Ordner", StringComparison.Ordinal))
        {
            if (TrySetStartupApproved(entry, enabled: false))
                return $"{entry.Name} deaktiviert (wie in den Windows-Einstellungen).";

            if (File.Exists(entry.Command))
            {
                var disabled = entry.Command + ".disabled";
                File.Move(entry.Command, disabled, overwrite: true);
                return $"{entry.Name} deaktiviert.";
            }

            return "Datei nicht gefunden.";
        }

        return "Eintragstyp nicht unterstützt.";
    }

    public string Enable(AutostartEntry entry)
    {
        if (entry.Enabled)
            return $"„{entry.Name}“ ist bereits aktiv.";

        if (entry.IsRegistry)
        {
            if (TrySetStartupApproved(entry, enabled: true))
            {
                // Ensure still present in Run (may have been KlarWin-backup-disabled)
                EnsureRegistryRunValue(entry);
                return $"{entry.Name} wieder aktiviert.";
            }

            return RestoreRegistryEntry(entry);
        }

        if (entry.Location.Contains("Ordner", StringComparison.Ordinal))
        {
            if (entry.Command.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            {
                var enabledPath = entry.Command[..^".disabled".Length];
                if (!File.Exists(entry.Command))
                    return "Datei nicht gefunden.";
                File.Move(entry.Command, enabledPath, overwrite: true);
                return $"{entry.Name} wieder aktiviert.";
            }

            if (TrySetStartupApproved(entry, enabled: true))
                return $"{entry.Name} wieder aktiviert.";
        }

        return "Eintragstyp nicht unterstützt.";
    }

    public string SetEnabled(AutostartEntry entry, bool enabled) =>
        enabled ? Enable(entry) : Disable(entry);

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
            return "Programm wurde nicht gefunden.";

        var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        Directory.CreateDirectory(startup);
        var linkName = SanitizeFileName(candidate.Name) + ".lnk";
        var linkPath = Path.Combine(startup, linkName);
        if (File.Exists(linkPath))
            return $"„{candidate.Name}“ startet bereits über den Autostart-Ordner.";

        try
        {
            CreateShortcut(linkPath, candidate.ExecutablePath, candidate.Name);
            TrySetStartupApproved(new AutostartEntry
            {
                Name = Path.GetFileNameWithoutExtension(linkName),
                Command = linkPath,
                Location = "Autostart-Ordner (Benutzer)",
                ApprovalKey = Path.GetFileName(linkPath),
                IsRegistry = false
            }, enabled: true);
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
            return null;

        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 1)
                return command[1..end];
        }

        var space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
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

    private static bool TrySetStartupApproved(AutostartEntry entry, bool enabled)
    {
        try
        {
            var keyName = string.IsNullOrWhiteSpace(entry.ApprovalKey) ? entry.Name : entry.ApprovalKey;
            var path = entry.Location.Contains("Ordner", StringComparison.Ordinal)
                ? ApprovedFolder
                : ApprovedRun;

            using var key = Registry.CurrentUser.CreateSubKey(path, true);
            if (key is null) return false;

            // Same binary layout Task Manager uses: 02 = on, 03 = off + FILETIME
            var data = new byte[12];
            data[0] = enabled ? (byte)2 : (byte)3;
            var fileTime = BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc());
            Buffer.BlockCopy(fileTime, 0, data, 4, 8);
            key.SetValue(keyName, data, RegistryValueKind.Binary);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool? ReadStartupApproved(string approvalKey, bool isFolder)
    {
        try
        {
            var path = isFolder ? ApprovedFolder : ApprovedRun;
            using var key = Registry.CurrentUser.OpenSubKey(path, false);
            if (key?.GetValue(approvalKey) is not byte[] data || data.Length == 0)
                return null;
            // 2 = enabled, 3 = disabled by user/Task Manager
            return data[0] switch
            {
                2 => true,
                3 => false,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private void EnsureRegistryRunValue(AutostartEntry entry)
    {
        try
        {
            var hive = entry.IsLocalMachine ? Registry.LocalMachine : Registry.CurrentUser;
            using var runKey = hive.OpenSubKey(RunKeyPath, true);
            if (runKey is null) return;
            if (runKey.GetValue(entry.Name) is null && !string.IsNullOrWhiteSpace(entry.Command))
                runKey.SetValue(entry.Name, entry.Command, RegistryValueKind.String);

            using var backup = Registry.CurrentUser.OpenSubKey(DisabledRunKey, true);
            backup?.DeleteValue(entry.Name, false);
        }
        catch
        {
            // ignore
        }
    }

    private string DisableRegistryEntry(AutostartEntry entry)
    {
        var hive = entry.IsLocalMachine ? Registry.LocalMachine : Registry.CurrentUser;
        try
        {
            using var runKey = hive.OpenSubKey(RunKeyPath, true);
            if (runKey is null)
                return "Registrierung nicht erreichbar. Als Administrator starten.";

            var command = runKey.GetValue(entry.Name)?.ToString() ?? entry.Command;
            runKey.DeleteValue(entry.Name, false);
            using var backup = Registry.CurrentUser.CreateSubKey(DisabledRunKey, true);
            backup?.SetValue(entry.Name, $"{(entry.IsLocalMachine ? "LM" : "CU")}|{command}", RegistryValueKind.String);
            return $"{entry.Name} deaktiviert.";
        }
        catch (UnauthorizedAccessException)
        {
            return "Keine Berechtigung. KlarWin als Administrator starten.";
        }
    }

    private string RestoreRegistryEntry(AutostartEntry entry)
    {
        try
        {
            using var backup = Registry.CurrentUser.OpenSubKey(DisabledRunKey, false);
            var payload = backup?.GetValue(entry.Name)?.ToString();
            if (string.IsNullOrWhiteSpace(payload))
                return "Backup-Eintrag nicht gefunden.";

            var parts = payload.Split('|', 2);
            if (parts.Length != 2)
                return "Backup-Eintrag ist ungültig.";

            var hive = parts[0] == "LM" ? Registry.LocalMachine : Registry.CurrentUser;
            using var runKey = hive.OpenSubKey(RunKeyPath, true);
            if (runKey is null)
                return "Registrierung nicht erreichbar. Als Administrator starten.";

            runKey.SetValue(entry.Name, parts[1], RegistryValueKind.String);
            using var backupWrite = Registry.CurrentUser.OpenSubKey(DisabledRunKey, true);
            backupWrite?.DeleteValue(entry.Name, false);
            TrySetStartupApproved(entry, enabled: true);
            return $"{entry.Name} wieder aktiviert.";
        }
        catch (UnauthorizedAccessException)
        {
            return "Keine Berechtigung. KlarWin als Administrator starten.";
        }
    }

    private static void AddDisabledRegistryBackups(List<AutostartEntry> list)
    {
        try
        {
            using var backup = Registry.CurrentUser.OpenSubKey(DisabledRunKey, false);
            if (backup is null) return;
            foreach (var name in backup.GetValueNames())
            {
                if (list.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && e.IsRegistry))
                    continue;

                var payload = backup.GetValue(name)?.ToString() ?? "";
                var parts = payload.Split('|', 2);
                var isLm = parts.Length == 2 && parts[0] == "LM";
                var command = parts.Length == 2 ? parts[1] : payload;
                list.Add(new AutostartEntry
                {
                    Name = name,
                    Command = command,
                    Location = isLm ? "Dieser PC (alle Benutzer)" : "Aktueller Benutzer",
                    Enabled = false,
                    IsRegistry = true,
                    IsLocalMachine = isLm,
                    ApprovalKey = name
                });
            }
        }
        catch
        {
            // ignore
        }
    }

    private static void AddRegistry(List<AutostartEntry> list, RegistryKey root, string path, string location, bool isLocalMachine)
    {
        try
        {
            using var key = root.OpenSubKey(path, false);
            if (key is null) return;
            foreach (var name in key.GetValueNames())
            {
                var value = key.GetValue(name)?.ToString() ?? "";
                var approved = isLocalMachine ? null : ReadStartupApproved(name, isFolder: false);
                list.Add(new AutostartEntry
                {
                    Name = name,
                    Command = value,
                    Location = location,
                    Enabled = approved ?? true,
                    IsRegistry = true,
                    IsLocalMachine = isLocalMachine,
                    ApprovalKey = name
                });
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
            if (file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(new AutostartEntry
                {
                    Name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file)),
                    Command = file,
                    Location = location,
                    Enabled = false,
                    ApprovalKey = Path.GetFileName(file)
                });
                continue;
            }

            var fileName = Path.GetFileName(file);
            var approved = ReadStartupApproved(fileName, isFolder: true);
            list.Add(new AutostartEntry
            {
                Name = Path.GetFileNameWithoutExtension(file),
                Command = file,
                Location = location,
                Enabled = approved ?? true,
                ApprovalKey = fileName
            });
        }
    }
}
