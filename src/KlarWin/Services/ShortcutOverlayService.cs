using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace KlarWin.Services;

public sealed class OverlayResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public bool NeedsAdmin { get; init; }
    public string? AppliedValue { get; init; }
}

public sealed class ShortcutOverlayService
{
    private const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons";
    private const string ValueName = "29";

    /// <summary>System icon references tried in order (Win10/11).</summary>
    private static readonly string[] SystemBlankIcons =
    [
        "%SystemRoot%\\System32\\imageres.dll,-17",
        "%SystemRoot%\\System32\\imageres.dll,-18",
        "%SystemRoot%\\System32\\shell32.dll,50",
        "%SystemRoot%\\System32\\shell32.dll,264"
    ];

    public OverlayResult HideArrows()
    {
        if (!IsAdministrator())
        {
            return new OverlayResult
            {
                Success = false,
                NeedsAdmin = true,
                Message = "Administratorrechte nötig. Oben rechts auf „Adminrechte freischalten“ klicken."
            };
        }

        try
        {
            var iconRef = ResolveBlankIconReference();
            using var key = Registry.LocalMachine.CreateSubKey(KeyPath, true)
                ?? throw new InvalidOperationException("Registrierungsschlüssel Shell Icons konnte nicht geöffnet werden.");
            key.SetValue(ValueName, iconRef, RegistryValueKind.String);

            ClearIconCache();
            RestartExplorer();
            NotifyShellChange();

            var verified = Registry.LocalMachine.OpenSubKey(KeyPath, false)?.GetValue(ValueName)?.ToString();
            return new OverlayResult
            {
                Success = !string.IsNullOrWhiteSpace(verified),
                AppliedValue = verified ?? iconRef,
                Message = !string.IsNullOrWhiteSpace(verified)
                    ? $"Verknüpfungspfeile ausgeblendet.\nRegistry: {verified}\n\nFalls Pfeile noch sichtbar sind: einmal abmelden oder PC neu starten. Windows-Updates setzen das manchmal zurück."
                    : "Registry-Eintrag konnte nicht bestätigt werden."
            };
        }
        catch (UnauthorizedAccessException)
        {
            return new OverlayResult
            {
                Success = false,
                NeedsAdmin = true,
                Message = "Zugriff verweigert — bitte KlarWin als Administrator starten."
            };
        }
        catch (Exception ex)
        {
            return new OverlayResult { Success = false, Message = ex.Message };
        }
    }

    public OverlayResult RestoreArrows()
    {
        if (!IsAdministrator())
        {
            return new OverlayResult
            {
                Success = false,
                NeedsAdmin = true,
                Message = "Administratorrechte nötig zum Wiederherstellen der Pfeile."
            };
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, true);
            if (key?.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, false);
            }

            ClearIconCache();
            RestartExplorer();
            NotifyShellChange();
            return new OverlayResult { Success = true, Message = "Verknüpfungspfeile sind wieder sichtbar." };
        }
        catch (UnauthorizedAccessException)
        {
            return new OverlayResult
            {
                Success = false,
                NeedsAdmin = true,
                Message = "Zugriff verweigert — bitte als Administrator starten."
            };
        }
        catch (Exception ex)
        {
            return new OverlayResult { Success = false, Message = ex.Message };
        }
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public bool AreArrowsHidden()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public string CurrentOverlaySetting()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, false);
            var value = key?.GetValue(ValueName)?.ToString();
            return string.IsNullOrWhiteSpace(value) ? "(Standard — Pfeile sichtbar)" : value;
        }
        catch
        {
            return "(nicht lesbar — Admin nötig)";
        }
    }

    public OverlayResult EnsureHiddenPreference(bool shouldHide)
    {
        if (!shouldHide) return new OverlayResult { Success = true, Message = "Pfeile sollen sichtbar bleiben." };
        if (!IsAdministrator()) return new OverlayResult { Success = false, NeedsAdmin = true, Message = "Automatisches Ausblenden beim Start braucht Admin." };
        if (AreArrowsHidden()) return new OverlayResult { Success = true, Message = "Pfeile sind bereits ausgeblendet." };
        return HideArrows();
    }

    private static string ResolveBlankIconReference()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var template in SystemBlankIcons)
        {
            var expanded = Environment.ExpandEnvironmentVariables(template);
            var dllPath = expanded.Split(',')[0];
            if (File.Exists(dllPath))
            {
                return expanded;
            }
        }

        var deployed = DeployBlankIco();
        return deployed + ",0";
    }

    private static string DeployBlankIco()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KlarWin");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, "blank-overlay.ico");

        var bundled = Path.Combine(AppContext.BaseDirectory, "Assets", "blank-overlay.ico");
        if (File.Exists(bundled))
        {
            File.Copy(bundled, target, overwrite: true);
            return target;
        }

        if (!File.Exists(target))
        {
            File.WriteAllBytes(target, MinimalBlankIco());
        }

        return target;
    }

    private static void ClearIconCache()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var ie4uinit = Path.Combine(windows, "System32", "ie4uinit.exe");
        if (File.Exists(ie4uinit))
        {
            try
            {
                RunHidden(ie4uinit, "-ClearIconCache");
            }
            catch
            {
                // optional
            }
        }
    }

    private static void RestartExplorer()
    {
        try
        {
            RunHidden("taskkill.exe", "/F /IM explorer.exe");
        }
        catch
        {
            foreach (var process in Process.GetProcessesByName("explorer"))
            {
                try { process.Kill(); } catch { /* ignore */ }
            }
        }

        Thread.Sleep(800);
        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            UseShellExecute = true
        });
    }

    private static void NotifyShellChange()
    {
        SHChangeNotify(0x08000000, 0x00001000, IntPtr.Zero, IntPtr.Zero);
    }

    private static void RunHidden(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        p?.WaitForExit(8000);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private static byte[] MinimalBlankIco()
    {
        // Fallback 16x16 transparent ICO
        return
        [
            0, 0, 1, 0, 1, 0, 16, 16, 0, 0, 1, 0, 32, 0, 104, 4, 0, 0, 22, 0, 0, 0, 40, 0, 0, 0, 16, 0, 0, 0, 32, 0, 0, 0, 1, 0, 32, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
        ];
    }
}
