using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using KlarWin.Services;

namespace KlarWin;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Global\GanzSoft.KlarWin.SingleInstance";
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Any(a => a.Equals(BrowserDownloadBridge.NativeArg, StringComparison.OrdinalIgnoreCase)))
        {
            // Chrome/Edge Native Messaging — no UI, no single-instance lock
            var code = BrowserDownloadBridge.RunNativeMessagingHost();
            Shutdown(code);
            return;
        }

        var postUpdate = e.Args.Any(a => a.Equals("--post-update", StringComparison.OrdinalIgnoreCase));

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                DiagnosticLogService.Error("Unbehandelte Ausnahme (AppDomain)", ex);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DiagnosticLogService.Error("Unbeobachtete Task-Ausnahme", args.Exception);
            args.SetObserved();
        };

        if (!TryAcquireSingleInstance(postUpdate))
        {
            ActivateExistingInstance();
            Shutdown();
            return;
        }

        if (postUpdate)
        {
            DesktopShortcutService.EnsureShortcutsPointToCanonical();
        }
        else if (TryRelaunchFromCanonicalInstall())
        {
            Shutdown();
            return;
        }
        else
        {
            DesktopShortcutService.EnsureShortcutsPointToCanonical();
        }

        DispatcherUnhandledException += (_, args) =>
        {
            DiagnosticLogService.Error("UI-Ausnahme", args.Exception);
            MessageBox.Show(
                "KlarWin ist unerwartet beendet worden:\n\n" + args.Exception.Message +
                $"\n\nProtokoll: {DiagnosticLogService.LogPath}",
                "KlarWin",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        DiagnosticLogService.Info($"Start {UpdateService.CurrentVersion}" +
            (postUpdate ? " (--post-update)" : ""));

        var main = new MainWindow();
        main.Show();

        if (KlarWinSettings.Load() is { StartLayout: { Enabled: true, ApplyOnLogin: true } })
        {
            _ = ApplyStartLayoutOnLoginAsync();
        }

        base.OnStartup(e);
    }

    private static async Task ApplyStartLayoutOnLoginAsync()
    {
        try
        {
            await Task.Delay(2500);
            var settings = KlarWinSettings.Load();
            if (!settings.StartLayout.Enabled
                || !settings.StartLayout.ApplyOnLogin
                || settings.StartLayout.Profiles.Count == 0)
                return;

            var context = WorkplaceContextService.Capture();
            var profile = StartLayoutService.SelectBestProfile(settings.StartLayout.Profiles, context);
            if (profile is null || profile.Apps.Count == 0) return;

            DiagnosticLogService.Info($"Startlayout automatisch: {profile.Name}");
            var log = await StartLayoutService.ApplyAsync(profile, context);
            DiagnosticLogService.Info(log);
        }
        catch (Exception ex)
        {
            DiagnosticLogService.Error("Startlayout beim Login", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseSingleInstanceForUpdate();
        base.OnExit(e);
    }

    /// <summary>Mutex vor Update-Neustart freigeben (Environment.Exit ruft OnExit nicht zuverlässig auf).</summary>
    public static void ReleaseSingleInstanceForUpdate()
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch
        {
            // ignore
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
    }

    private static bool TryRelaunchFromCanonicalInstall()
    {
        try
        {
            var canonical = DesktopShortcutService.CanonicalInstallExePath();
            var running = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(running) || !File.Exists(canonical)) return false;
            if (string.Equals(
                    Path.GetFullPath(canonical),
                    Path.GetFullPath(running),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var canonicalVer = UpdateService.ReadExeVersion(canonical);
            var runningVer = UpdateService.CurrentVersion;
            if (!Version.TryParse(NormalizeVersion(canonicalVer), out var cv)
                || !Version.TryParse(NormalizeVersion(runningVer), out var rv))
            {
                return false;
            }

            if (cv <= rv) return false;

            Process.Start(new ProcessStartInfo
            {
                FileName = canonical,
                Arguments = "--post-update",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(canonical) ?? ""
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeVersion(string version)
    {
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        while (parts.Length < 3) parts = [.. parts, "0"];
        return string.Join('.', parts.Take(4));
    }

    private static bool TryAcquireSingleInstance(bool postUpdate)
    {
        var attempts = postUpdate ? 80 : 1;
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                _singleInstanceMutex?.Dispose();
                _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
                if (createdNew) return true;
            }
            catch
            {
                return true;
            }

            if (!postUpdate) break;

            if (i == 30)
            {
                TerminateOtherKlarWinProcesses();
            }

            Thread.Sleep(250);
        }

        if (postUpdate)
        {
            TerminateOtherKlarWinProcesses();
            Thread.Sleep(500);
            try
            {
                _singleInstanceMutex?.Dispose();
                _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
                if (createdNew) return true;
            }
            catch
            {
                return true;
            }
        }

        return false;
    }

    private static void TerminateOtherKlarWinProcesses()
    {
        var current = Process.GetCurrentProcess().Id;
        foreach (var process in Process.GetProcessesByName("KlarWin"))
        {
            if (process.Id == current) continue;
            try
            {
                process.Kill(true);
            }
            catch
            {
                // best effort
            }
        }
    }

    private static void ActivateExistingInstance()
    {
        var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            if (process.Id == current.Id) continue;
            try
            {
                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero)
                {
                    process.Refresh();
                    handle = process.MainWindowHandle;
                }

                if (handle != IntPtr.Zero)
                {
                    if (IsIconic(handle))
                    {
                        ShowWindow(handle, SwRestore);
                    }

                    SetForegroundWindow(handle);
                    return;
                }
            }
            catch
            {
                // best effort
            }
        }
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
}
