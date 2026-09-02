using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace KlarWin.Services;

public enum ElevationAttempt
{
    Relaunched,
    Cancelled,
    Failed
}

public static class Elevation
{
    private const int ErrorCancelled = 1223;

    /// <summary>Starts KlarWin elevated and closes the current instance. UAC prompt is shown by Windows.</summary>
    public static ElevationAttempt TryRelaunchAsAdministrator()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return ElevationAttempt.Failed;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                Verb = "runas"
            });
            Application.Current.Shutdown();
            return ElevationAttempt.Relaunched;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return ElevationAttempt.Cancelled;
        }
        catch
        {
            return ElevationAttempt.Failed;
        }
    }

    public static void RelaunchAsAdministrator() => TryRelaunchAsAdministrator();

    /// <summary>Starts KlarWin without elevation and closes the current instance.</summary>
    public static ElevationAttempt TryRelaunchAsNormalUser()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return ElevationAttempt.Failed;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
            Application.Current.Shutdown();
            return ElevationAttempt.Relaunched;
        }
        catch
        {
            return ElevationAttempt.Failed;
        }
    }
}
