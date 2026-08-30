using System.Diagnostics;

namespace KlarWin.Services;

public sealed class SpeedResult
{
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed class SpeedService
{
    public SpeedResult Apply()
    {
        var notes = new List<string>();
        SetHighPerformancePlan(notes);
        FlushDns(notes);
        ReduceAnimations(notes);
        return new SpeedResult { Notes = notes };
    }

    private static void SetHighPerformancePlan(List<string> notes)
    {
        // Hochleistung
        var code = Run("powercfg", "/setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        if (code == 0)
        {
            notes.Add("Energieplan auf Hochleistung gestellt.");
            return;
        }

        notes.Add("Energieplan konnte nicht geändert werden. Als Administrator starten.");
    }

    private static void FlushDns(List<string> notes)
    {
        var code = Run("ipconfig", "/flushdns");
        notes.Add(code == 0 ? "DNS-Cache geleert." : "DNS-Cache konnte nicht geleert werden.");
    }

    private static void ReduceAnimations(List<string> notes)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", true)
                ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects");
            key.SetValue("VisualFXSetting", 2, Microsoft.Win32.RegistryValueKind.DWord);
            notes.Add("Visuelle Effekte auf Leistung gestellt. Nach dem Abmelden vollständig wirksam.");
        }
        catch (Exception ex)
        {
            notes.Add($"Visuelle Effekte: {ex.Message}");
        }
    }

    private static int Run(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        process?.WaitForExit(8000);
        return process?.ExitCode ?? 1;
    }
}
