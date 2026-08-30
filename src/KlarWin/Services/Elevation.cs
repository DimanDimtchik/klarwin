using System.Diagnostics;
using System.Windows;

namespace KlarWin.Services;

public static class Elevation
{
    public static void RelaunchAsAdministrator()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
            Verb = "runas"
        });
        Application.Current.Shutdown();
    }
}
