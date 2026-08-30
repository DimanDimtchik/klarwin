using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace KlarWin.Services;

public sealed class OverlayResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public bool NeedsAdmin { get; init; }
}

public sealed class ShortcutOverlayService
{
    private const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons";

    public OverlayResult HideArrows()
    {
        try
        {
            var icon = EnsureBlankIcon();
            using var key = Registry.LocalMachine.CreateSubKey(KeyPath, true)
                ?? throw new InvalidOperationException("Registrierungsschlüssel konnte nicht geöffnet werden.");
            key.SetValue("29", icon + ",0", RegistryValueKind.String);
            RestartExplorer();
            return new OverlayResult
            {
                Success = true,
                Message = "Verknüpfungspfeile sind ausgeblendet. Nach einem Windows-Update ggf. erneut klicken."
            };
        }
        catch (UnauthorizedAccessException)
        {
            return new OverlayResult
            {
                Success = false,
                NeedsAdmin = true,
                Message = "Dafür braucht KlarWin Administratorrechte."
            };
        }
        catch (Exception ex)
        {
            return new OverlayResult { Success = false, Message = ex.Message };
        }
    }

    public OverlayResult RestoreArrows()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, true);
            key?.DeleteValue("29", false);
            RestartExplorer();
            return new OverlayResult { Success = true, Message = "Verknüpfungspfeile sind wieder sichtbar." };
        }
        catch (UnauthorizedAccessException)
        {
            return new OverlayResult
            {
                Success = false,
                NeedsAdmin = true,
                Message = "Dafür braucht KlarWin Administratorrechte."
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

    private static string EnsureBlankIcon()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KlarWin");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "blank.ico");
        File.WriteAllBytes(path, BlankIconBytes());
        return path;
    }

    private static void RestartExplorer()
    {
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            try
            {
                process.Kill();
            }
            catch
            {
                // Explorer startet Windows selbst oft neu.
            }
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            UseShellExecute = true
        });
    }

    private static byte[] BlankIconBytes()
    {
        // 16x16 transparent ICO, AND-mask only.
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write((short)0);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write((byte)16);
        writer.Write((byte)16);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((short)1);
        writer.Write((short)32);
        var imageSizePosition = stream.Position;
        writer.Write(0);
        writer.Write(22);
        var imageStart = stream.Position;
        writer.Write(40);
        writer.Write(16);
        writer.Write(32);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        for (var i = 0; i < 16 * 16; i++)
        {
            writer.Write(0);
        }

        for (var i = 0; i < 64; i++)
        {
            writer.Write((byte)0xFF);
        }

        var imageSize = (int)(stream.Position - imageStart);
        stream.Position = imageSizePosition;
        writer.Write(imageSize);
        return stream.ToArray();
    }
}
