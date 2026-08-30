using System.Windows;
using System.Windows.Threading;
using KlarWin.Services;

namespace KlarWin;

public partial class MainWindow : Window
{
    private readonly PerformanceService _performance = new();
    private readonly CleanupService _cleanup = new();
    private readonly SpeedService _speed = new();
    private readonly ShortcutOverlayService _overlay = new();
    private readonly DispatcherTimer _timer;
    private string _mode = "";

    public MainWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshPerformance();
        Loaded += (_, _) =>
        {
            RefreshPerformance();
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _performance.Dispose();
        };
    }

    private void RefreshPerformance()
    {
        var snap = _performance.Capture();
        CpuBar.Width = BarWidth(snap.CpuPercent);
        RamBar.Width = BarWidth(snap.RamPercent);
        DiskBar.Width = BarWidth(100 - snap.DiskFreePercent);
        CpuLabel.Text = $"{snap.CpuPercent:0}%";
        RamLabel.Text = $"{snap.RamPercent:0}%";
        DiskLabel.Text = $"{snap.DiskFreePercent:0}% frei";
    }

    private static double BarWidth(double percent) => Math.Max(0, Math.Min(220, 220 * percent / 100));

    private void OnCleanClick(object sender, RoutedEventArgs e)
    {
        _mode = "clean";
        PanelTitle.Text = "Speicher bereinigen";
        PanelBody.Text = "Löscht nur temporäre Dateien und leert den Papierkorb. Fotos, Dokumente und Programme bleiben.";
        PrimaryAction.Content = "Jetzt bereinigen";
        PrimaryAction.Visibility = Visibility.Visible;
        SecondaryAction.Visibility = Visibility.Collapsed;
    }

    private void OnSpeedClick(object sender, RoutedEventArgs e)
    {
        _mode = "speed";
        PanelTitle.Text = "Windows beschleunigen";
        PanelBody.Text = "Stellt Hochleistung ein, reduziert Animationen und leert den DNS-Cache.";
        PrimaryAction.Content = "Tempo anwenden";
        PrimaryAction.Visibility = Visibility.Visible;
        SecondaryAction.Visibility = Visibility.Collapsed;
    }

    private void OnPerfClick(object sender, RoutedEventArgs e)
    {
        _mode = "perf";
        var snap = _performance.Capture();
        PanelTitle.Text = "Leistung";
        PanelBody.Text = $"CPU {snap.CpuPercent:0}% · RAM {CleanupService.FormatBytes(snap.RamUsedBytes)} von {CleanupService.FormatBytes(snap.RamTotalBytes)} · {snap.DiskName} noch {CleanupService.FormatBytes(snap.DiskFreeBytes)} frei";
        PrimaryAction.Visibility = Visibility.Collapsed;
        SecondaryAction.Visibility = Visibility.Collapsed;
    }

    private void OnOverlayClick(object sender, RoutedEventArgs e)
    {
        _mode = "overlay";
        PanelTitle.Text = "Verknüpfungspfeile";
        PanelBody.Text = "Blendet den kleinen Pfeil auf Desktop-Verknüpfungen aus. Windows-Updates setzen ihn oft wieder.";
        PrimaryAction.Content = "Pfeile ausblenden";
        SecondaryAction.Content = "Pfeile wiederherstellen";
        PrimaryAction.Visibility = Visibility.Visible;
        SecondaryAction.Visibility = Visibility.Visible;
    }

    private async void OnPrimaryAction(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case "clean":
                await RunCleanup();
                break;
            case "speed":
                RunSpeed();
                break;
            case "overlay":
                ApplyOverlay(hide: true);
                break;
        }
    }

    private void OnSecondaryAction(object sender, RoutedEventArgs e)
    {
        if (_mode == "overlay")
        {
            ApplyOverlay(hide: false);
        }
    }

    private async Task RunCleanup()
    {
        PrimaryAction.IsEnabled = false;
        PanelBody.Text = "Aufräumen läuft …";
        var progress = new Progress<string>(text => PanelBody.Text = text);
        var result = await Task.Run(() => _cleanup.CleanNow(progress));
        var extra = result.Notes.Count > 0 ? " " + string.Join(" ", result.Notes) : "";
        PanelBody.Text = $"{result.FilesRemoved} Dateien entfernt, {CleanupService.FormatBytes(result.BytesRemoved)} frei.{extra}";
        PrimaryAction.IsEnabled = true;
    }

    private void RunSpeed()
    {
        var result = _speed.Apply();
        PanelBody.Text = string.Join(" ", result.Notes);
    }

    private void ApplyOverlay(bool hide)
    {
        var result = hide ? _overlay.HideArrows() : _overlay.RestoreArrows();
        if (result.NeedsAdmin && !ShortcutOverlayService.IsAdministrator())
        {
            var answer = MessageBox.Show(
                this,
                "KlarWin muss einmal als Administrator starten, um die Desktop-Pfeile zu ändern. Jetzt neu starten?",
                "KlarWin",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
            {
                Elevation.RelaunchAsAdministrator();
            }
            return;
        }

        PanelBody.Text = result.Message;
    }
}
