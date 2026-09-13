using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using KlarWin.Services;
using Microsoft.Win32;

namespace KlarWin;

public sealed class DetailRow
{
    public string Col1 { get; init; } = "";
    public string Col2 { get; set; } = "";
    public string Col3 { get; set; } = "";
    public object? Tag { get; init; }
    public bool IsChecked { get; set; }
}

public partial class MainWindow : Window
{
    private readonly KlarWinSettings _settings = KlarWinSettings.Load();
    private readonly PerformanceMonitor _performanceMonitor = new();
    private readonly SystemHealthTracker _healthTracker = new();
    private readonly CleanupService _cleanup = new();
    private readonly SpeedService _speed = new();
    private readonly ShortcutOverlayService _overlay = new();
    private readonly AutostartService _autostart;
    private readonly SecureWipeService _wipe = new();
    private readonly RecoveryService _recovery = new();
    private readonly LicenseKeyService _keys = new();
    private readonly NetworkMonitorService _network = new();
    private readonly AiLoadService _ai = new();
    private readonly UpdateService _update = new();
    private readonly SoftwareUpdateService _softwareUpdates = new();
    private UpdateInfo? _pendingUpdate;
    private IReadOnlyList<SoftwareUpdateItem> _softwareUpdateItems = [];
    private readonly DispatcherTimer _timer;
    private string _mode = "";
    private string _aiFilter = "all";
    private bool _filterUpdating;
    private int _liveBusy;
    private DateTime _lastRouterUtc = DateTime.MinValue;
    private DateTime _lastHostsUtc = DateTime.MinValue;
    private DateTime _lastPingUtc = DateTime.MinValue;
    private CancellationTokenSource? _wipeCts;
    private IReadOnlyList<AutostartEntry> _autostartItems = [];
    private IReadOnlyList<RecoverableItem> _recoverItems = [];
    private IReadOnlyList<LicenseKeyEntry> _keyItems = [];
    private CleanupPreview? _cleanupPreview;
    private readonly Dictionary<string, CheckBox> _optionChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CheckBox> _itemChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Button[] _tileButtons;
    private readonly DispatcherTimer _promoTimer;
    private int _promoSlideIndex;
    private FeaturePanelWindow? _featurePanel;
    private SettingsWindow? _settingsWindow;
    private Canvas? _detailCpuChart;
    private Canvas? _detailRamChart;
    private Canvas? _detailDiskChart;
    private TextBlock? _detailCpuValue;
    private TextBlock? _detailRamValue;
    private TextBlock? _detailDiskValue;
    private StackPanel? _detailHealthSection;
    private TextBlock? _detailHealthOverall;
    private StackPanel? _detailHealthCategories;
    private StackPanel? _detailHealthHints;
    private TextBlock? _detailHealthDisclaimer;
    private StackPanel? _detailTrendSection;
    private Canvas? _detailTrendCpuChart;
    private Canvas? _detailTrendRamChart;
    private Canvas? _detailTrendTempChart;
    private TextBlock? _detailTrendHint;
    private StackPanel? _detailTempSection;
    private TextBlock? _detailTempUnavailable;
    private TextBlock? _detailTempHint;
    private TextBlock? _detailTempDiagBlock;
    private bool _perfDiagVisible;
    private ComboBox? _detailDiskVolumeBox;
    private bool _detailDiskRefreshing;
    private char? _selectedDiskLetter;
    private int _lastVolumeCount;
    private readonly Dictionary<string, (TextBlock Value, Canvas Chart, StackPanel Panel)> _detailTempRows = new(StringComparer.OrdinalIgnoreCase);
    private WindowsIsoRecommendation _windowsIsoRecommendation =
        WindowsIsoRecommendationService.Build(WindowsPcProfileService.Read());

    private static readonly (string Title, string Body, string Bullet)[] PromoSlides =
    [
        ("BUCHFÜHRUNG. KLAR IM GRIFF.", "Einnahmen, Ausgaben und Belege übersichtlich — für Handwerk und Dienstleistung.", "Belege · Ausgaben · EÜR-Vorbereitung"),
        ("TERMINE. NICHTS GEHT UNTER.", "Termine, Einsätze und Erinnerungen an einem Ort — für Sie und Ihr Team.", "Termine · Erinnerungen · Team-Kalender"),
        ("ZEIT ERFASSEN. AUFTRÄGE ZUORDNEN.", "Arbeitszeiten sauber dokumentieren — für Abrechnung und den Überblick.", "Stunden · Aufträge · Auswertung"),
        ("KUNDEN & AUFTRÄGE. EIN SYSTEM.", "DG CRM für Handwerk und Dienstleistung — auf ganz-soft.de.", "Kunden · Angebote · Aufträge")
    ];

    public MainWindow()
    {
        _autostart = new AutostartService(_settings);
        InitializeComponent();
        PopulateTileFeatureLists();
        _tileButtons =
        [
            TileClean, TileSpeed, TilePerf, TileOverlay, TileAutostart, TileWipe,
            TileRecover, TileKeys, TileNetwork, TileAi, TileUpdate, TileBackup, TileUsb, TileTransfer
        ];
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => _ = RefreshLiveAsync();
        _promoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _promoTimer.Tick += (_, _) => AdvancePromoSlide();
        Loaded += (_, _) =>
        {
            ThemeService.Apply(this, _settings);
            ApplyPromoBranding();
            VersionLabel.Text = $"Version {UpdateService.CurrentVersion}";
            UpdateAdminUi();
            ApplyPromoSlide(0, animate: false);
            _promoTimer.Start();
            EnsureWindowOnScreen();
            UpdateTileGridColumns();
            if (_settings.HideShortcutArrows && ShortcutOverlayService.IsAdministrator())
            {
                _overlay.EnsureHiddenPreference(true);
            }
            _ = RefreshLiveAsync();
            _timer.Start();
            _ = QuietUpdateCheckAsync();
        };
        SizeChanged += (_, _) => UpdateTileGridColumns();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _promoTimer.Stop();
            _wipeCts?.Cancel();
            _network.Dispose();
            _performanceMonitor.Dispose();
            CloseFeaturePanel();
        };
    }

    private void PopulateTileFeatureLists()
    {
        FeatureListUi.Fill(TileCleanFeatures, FeatureCatalog.Clean);
        FeatureListUi.Fill(TileSpeedFeatures, FeatureCatalog.Speed);
        FeatureListUi.Fill(TilePerfFeatures, FeatureCatalog.Performance);
        FeatureListUi.Fill(TileOverlayFeatures, FeatureCatalog.Overlay);
        FeatureListUi.Fill(TileAutostartFeatures, FeatureCatalog.Autostart);
        FeatureListUi.Fill(TileWipeFeatures, FeatureCatalog.Wipe);
        FeatureListUi.Fill(TileRecoverFeatures, FeatureCatalog.Recover);
        FeatureListUi.Fill(TileKeysFeatures, FeatureCatalog.Keys);
        FeatureListUi.Fill(TileNetworkFeatures, FeatureCatalog.Network);
        FeatureListUi.Fill(TileAiFeatures, FeatureCatalog.AiLoad);
        FeatureListUi.Fill(TileUpdateFeatures, FeatureCatalog.Updates);
        FeatureListUi.Fill(TileBackupFeatures, FeatureCatalog.Backup);
        FeatureListUi.Fill(TileUsbFeatures, FeatureCatalog.UsbStick);
        FeatureListUi.Fill(TileTransferFeatures, FeatureCatalog.TransferFolders);
    }

    private void UpdateAdminUi()
    {
        var isAdmin = ShortcutOverlayService.IsAdministrator();
        AdminElevateButton.Content = isAdmin ? "Administrator aktiv" : "Adminrechte freischalten";
        AdminElevateButton.ToolTip = isAdmin
            ? "Klicken, um KlarWin ohne Administratorrechte neu zu starten"
            : "KlarWin mit Administratorrechten neu starten (UAC-Abfrage)";
    }

    private void OnAdminElevateClick(object sender, RoutedEventArgs e)
    {
        if (ShortcutOverlayService.IsAdministrator())
        {
            var answer = MessageBox.Show(
                this,
                "KlarWin ohne Administratorrechten neu starten?\n\nEinige Funktionen (z. B. Windows-Temp bereinigen) sind dann eingeschränkt.",
                "KlarWin",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
            {
                RequestNormalUserRelaunch();
            }

            return;
        }

        RequestAdminRelaunch();
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // DragMove fails while window state is changing
        }
    }

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button or TextBoxBase or ComboBox or ListBox or ScrollBar) return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void EnsureWindowOnScreen()
    {
        if (WindowState != WindowState.Normal) return;

        var work = SystemParameters.WorkArea;
        var maxWidth = Math.Max(MinWidth, work.Width - 40);
        var maxHeight = Math.Max(MinHeight, work.Height - 40);
        if (Width > maxWidth) Width = maxWidth;
        if (Height > maxHeight) Height = maxHeight;

        if (Top < work.Top) Top = work.Top + 8;
        if (Left < work.Left) Left = work.Left + 8;
        if (Top + Height > work.Bottom) Top = Math.Max(work.Top + 8, work.Bottom - Height);
        if (Left + Width > work.Right) Left = Math.Max(work.Left + 8, work.Right - Width);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        CloseFeaturePanel();

        if (_settingsWindow is { IsLoaded: true })
        {
            if (!_settingsWindow.IsVisible)
            {
                _settingsWindow.Show();
            }

            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings, () =>
        {
            ThemeService.Apply(this, _settings);
            UpdateAdminUi();
        })
        {
            Owner = this,
            ShowInTaskbar = true
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void ApplyPromoBranding()
    {
        var wordmark = GanzSoftBranding.LoadWordmark();
        PromoLeftLogo.Source = wordmark;
        PromoRightLogo.Source = wordmark;
        PromoBottomBrand.Source = wordmark;
        HeaderLogo.Source = GanzSoftBranding.LoadKlarWinIcon();
    }

    private void OnScrollViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer) return;
        scrollViewer.ScrollToVerticalOffset(Math.Max(0, scrollViewer.VerticalOffset - e.Delta));
        e.Handled = true;
    }

    private Window GetDialogOwner() =>
        _featurePanel is { IsVisible: true } ? _featurePanel : this;

    private async Task<T> RunWithInstallProgressAsync<T>(int totalHint, Func<IProgress<SoftwareInstallProgress>, Task<T>> work)
    {
        var owner = GetDialogOwner();
        var wasEnabled = owner.IsEnabled;
        owner.IsEnabled = false;
        var progressWin = new InstallProgressWindow { Owner = owner };
        progressWin.Apply(new SoftwareInstallProgress
        {
            Total = totalHint,
            Headline = "Installation startet …",
            Detail = "Bitte warten.",
            IsIndeterminate = true
        });
        progressWin.Show();

        var progress = new Progress<SoftwareInstallProgress>(p =>
        {
            Dispatcher.Invoke(() => progressWin.Apply(p));
        });

        try
        {
            return await work(progress);
        }
        finally
        {
            owner.IsEnabled = wasEnabled;
            progressWin.Close();
        }
    }

    private void OnPromoClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://ganz-soft.de/",
                UseShellExecute = true
            });
        }
        catch
        {
            // ignore
        }
    }

    public void ApplyChromeTheme(KlarWinSettings settings)
    {
        BrandTitle.FontFamily = new System.Windows.Media.FontFamily(
            string.IsNullOrWhiteSpace(settings.FontFamilyTitle) ? "Bahnschrift" : settings.FontFamilyTitle);
        BrandTitle.FontSize = Math.Clamp(settings.FontSizeTitle, 28, 96);
        BrandTitle.Foreground = new System.Windows.Media.SolidColorBrush(
            KlarWinSettings.ParseColor(settings.InkColor, System.Windows.Media.Color.FromRgb(0x18, 0x24, 0x1F)));
        BrandSubtitle.Foreground = new System.Windows.Media.SolidColorBrush(
            KlarWinSettings.ParseColor(settings.MuteColor, System.Windows.Media.Color.FromRgb(0x4A, 0x5C, 0x54)));
        VersionLabel.Foreground = new System.Windows.Media.SolidColorBrush(
            KlarWinSettings.ParseColor(settings.AccentColor, System.Windows.Media.Color.FromRgb(0x8C, 0x62, 0x39)));

        var pos = settings.PromoPosition ?? "Bottom";
        PromoBottom.Visibility = pos.Equals("Bottom", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
        PromoLeft.Visibility = pos.Equals("Left", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
        PromoRight.Visibility = pos.Equals("Right", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AdvancePromoSlide()
    {
        _promoSlideIndex = (_promoSlideIndex + 1) % PromoSlides.Length;
        ApplyPromoSlide(_promoSlideIndex, animate: true);
    }

    private void ApplyPromoSlide(int index, bool animate)
    {
        index = ((index % PromoSlides.Length) + PromoSlides.Length) % PromoSlides.Length;
        _promoSlideIndex = index;
        var slide = PromoSlides[index];

        void SetText()
        {
            PromoBottomTitle.Text = slide.Title;
            PromoBottomBody.Text = slide.Body;
            PromoBottomBullet.Text = slide.Bullet;
            PromoLeftTitle.Text = slide.Title;
            PromoLeftBody.Text = slide.Body;
            PromoLeftBullet.Text = slide.Bullet;
            PromoRightTitle.Text = slide.Title;
            PromoRightBody.Text = slide.Body;
            PromoRightBullet.Text = slide.Bullet;
        }

        if (!animate)
        {
            SetText();
            PromoBottomTransform.X = 0;
            PromoLeftTransform.X = 0;
            PromoRightTransform.X = 0;
            return;
        }

        var distance = 260.0;
        var outDuration = TimeSpan.FromMilliseconds(700);
        var inDuration = TimeSpan.FromMilliseconds(700);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        var completed = 0;

        void OnOutComplete()
        {
            if (Interlocked.Increment(ref completed) < 3) return;
            SetText();
            AnimateIn(PromoBottomTransform, distance, inDuration, ease);
            AnimateIn(PromoLeftTransform, 120, inDuration, ease);
            AnimateIn(PromoRightTransform, 120, inDuration, ease);
        }

        AnimateOut(PromoBottomTransform, distance, outDuration, ease, OnOutComplete);
        AnimateOut(PromoLeftTransform, 120, outDuration, ease, OnOutComplete);
        AnimateOut(PromoRightTransform, 120, outDuration, ease, OnOutComplete);
    }

    private static void AnimateOut(TranslateTransform transform, double distance, TimeSpan duration, IEasingFunction ease, Action onComplete)
    {
        var anim = new DoubleAnimation(0, -distance, duration) { EasingFunction = ease };
        anim.Completed += (_, _) => onComplete();
        transform.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private static void AnimateIn(TranslateTransform transform, double distance, TimeSpan duration, IEasingFunction ease)
    {
        transform.X = distance;
        var anim = new DoubleAnimation(distance, 0, duration) { EasingFunction = ease };
        transform.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void UpdateActiveTileHighlight()
    {
        Button? active = _mode switch
        {
            "clean" => TileClean,
            "speed" => TileSpeed,
            "perf" => TilePerf,
            "overlay" => TileOverlay,
            "autostart" => TileAutostart,
            "wipe" => TileWipe,
            "recover" => TileRecover,
            "keys" => TileKeys,
            "network" => TileNetwork,
            "ai" => TileAi,
            "software-updates" => TileUpdate,
            "backup" => TileBackup,
            "usb" => TileUsb,
            "transfer" => TileTransfer,
            _ => null
        };

        foreach (var tile in _tileButtons)
        {
            tile.Tag = tile == active ? "active" : "";
        }
    }

    private void RequestAdminRelaunch()
    {
        if (ShortcutOverlayService.IsAdministrator())
        {
            return;
        }

        switch (Elevation.TryRelaunchAsAdministrator())
        {
            case ElevationAttempt.Cancelled:
                PanelBody.Text = "Administrator-Anfrage abgebrochen. Sie können es jederzeit über „Adminrechte freischalten“ erneut versuchen.";
                break;
            case ElevationAttempt.Failed:
                MessageBox.Show(
                    this,
                    "KlarWin konnte nicht mit Administratorrechten neu gestartet werden.\n\nAlternativ: Rechtsklick auf KlarWin → Als Administrator ausführen.",
                    "KlarWin",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                break;
        }
    }

    private void RequestNormalUserRelaunch()
    {
        if (Elevation.TryRelaunchAsNormalUser() == ElevationAttempt.Failed)
        {
            MessageBox.Show(
                this,
                "KlarWin konnte nicht ohne Administratorrechte neu gestartet werden.",
                "KlarWin",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void PromptAdminElevation(string message)
    {
        if (ShortcutOverlayService.IsAdministrator())
        {
            return;
        }

        var answer = MessageBox.Show(this, message, "KlarWin", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            RequestAdminRelaunch();
        }
    }

    private async Task RefreshLiveAsync()
    {
        var live = _performanceMonitor.Sample();
        var perf = live.Snapshot;
        CpuLabel.Text = $"{perf.CpuPercent:0} %";
        RamLabel.Text = $"{perf.RamPercent:0} %";
        TempLabel.Text = live.PrimaryCpuTempCelsius is double temp ? $"{temp:0} °C" : "—";

        SparklineRenderer.Draw(TilePerfCpuChart, _performanceMonitor.CpuHistory.Snapshot(), 100,
            Color.FromRgb(0x2F, 0x6A, 0x4A));
        SparklineRenderer.DrawAutoScale(TilePerfRamChart, _performanceMonitor.RamHistory.Snapshot(),
            Color.FromRgb(0x2F, 0x6A, 0x4A));
        UpdateTileTemperatureChart(live);
        UpdateTileHealthSummary(live);

        var net = _network.ReadLocal();
        ApplyNetworkBars(net);
        var ai = _ai.Capture(_aiFilter, perf.CpuPercent, perf.RamTotalBytes);
        ApplyAiBars(ai);

        if (_mode == "perf")
        {
            EnsurePerfDetailCharts();
            UpdatePerfDetailCharts(live);
        }
        else if (_mode == "network")
        {
            ShowNetworkPanel(net, refreshList: false);
        }
        else if (_mode == "ai")
        {
            UpdateAiLive(ai);
        }

        if (Interlocked.Exchange(ref _liveBusy, 1) == 1) return;
        try
        {
            if ((DateTime.UtcNow - _lastPingUtc).TotalSeconds >= 2)
            {
                _lastPingUtc = DateTime.UtcNow;
                await _network.PingGatewayAsync();
            }

            var wantHosts = _mode == "network" && (DateTime.UtcNow - _lastHostsUtc).TotalSeconds >= 20;
            if ((DateTime.UtcNow - _lastRouterUtc).TotalSeconds >= 2)
            {
                _lastRouterUtc = DateTime.UtcNow;
                if (wantHosts) _lastHostsUtc = DateTime.UtcNow;
                var router = await _network.RefreshRouterAsync(wantHosts);
                ApplyNetworkBars(router);
                if (_mode == "network")
                {
                    ShowNetworkPanel(router, refreshList: wantHosts);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _liveBusy, 0);
        }
    }

    private void ApplyNetworkBars(NetworkSnapshot net)
    {
        var down = net.RouterReachable ? net.WanDownBytesPerSec : net.AdapterDownBytesPerSec;
        var up = net.RouterReachable ? net.WanUpBytesPerSec : net.AdapterUpBytesPerSec;
        var downMaxBytes = net.WanDownBitsMax > 0 ? net.WanDownBitsMax / 8.0 : Math.Max(net.AdapterSpeedBits / 8.0, 1);
        var upMaxBytes = net.WanUpBitsMax > 0 ? net.WanUpBitsMax / 8.0 : Math.Max(net.AdapterSpeedBits / 8.0, 1);
        NetDownBar.Width = BarWidth(100.0 * down / downMaxBytes);
        NetUpBar.Width = BarWidth(100.0 * up / upMaxBytes);
        NetDownLabel.Text = NetworkMonitorService.FormatRate(down);
        NetUpLabel.Text = NetworkMonitorService.FormatRate(up);
        NetPingLabel.Text = net.PingMs is int ms ? $"{ms} ms · {net.Gateway}" : (string.IsNullOrWhiteSpace(net.Gateway) ? "kein Gateway" : net.Gateway);
    }

    private void ApplyAiBars(AiLoadSnapshot ai)
    {
        AiCpuBar.Width = BarWidth(ai.CpuPercent);
        AiRamBar.Width = BarWidth(ai.RamPercent);
        AiGpuBar.Width = BarWidth(ai.GpuPercent);
        AiCpuLabel.Text = $"{ai.CpuPercent:0}%";
        AiRamLabel.Text = $"{ai.RamPercent:0}%";
        AiGpuLabel.Text = ai.GpuAvailable ? $"{ai.GpuPercent:0}%" : "—";
    }

    private static string FormatDriveSummary(string diskName, long freeBytes, long totalBytes)
    {
        var letter = diskName.TrimEnd(':').Trim();
        return $"{letter}: {ByteSizeFormatter.Format(freeBytes, ByteSizeKind.Disk)} frei von {ByteSizeFormatter.Format(totalBytes, ByteSizeKind.Disk)}";
    }

    private static double BarWidth(double percent) => Math.Max(0, Math.Min(180, 180 * percent / 100));

    private void ResetPanelChrome()
    {
        WipeModeBox.Visibility = Visibility.Collapsed;
        FilterBox.Visibility = Visibility.Collapsed;
        OptionsPanel.Visibility = Visibility.Collapsed;
        SpeedInputPanel.Visibility = Visibility.Collapsed;
        NetworkNotePanel.Visibility = Visibility.Collapsed;
        UsbFormatPanel.Visibility = Visibility.Collapsed;
        HidePanelProgress();
        OptionsPanel.Children.Clear();
        _optionChecks.Clear();
        _itemChecks.Clear();
        DetailGrid.Visibility = Visibility.Collapsed;
        DetailGrid.ItemsSource = null;
        SecondaryAction.Visibility = Visibility.Collapsed;
        TertiaryAction.Visibility = Visibility.Collapsed;
        QuaternaryAction.Visibility = Visibility.Collapsed;
        PrimaryAction.Visibility = Visibility.Visible;
        PrimaryAction.IsEnabled = true;
        PerfDetailPanel.Visibility = Visibility.Collapsed;
        PanelBody.Visibility = Visibility.Visible;
        _perfDiagVisible = false;
    }

    private static UIElement BuildCheckboxContent(string label, ImageSource? icon)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon is not null)
        {
            panel.Children.Add(new Image
            {
                Source = icon,
                Width = 18,
                Height = 18,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = label,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }

    private StackPanel ActiveContentHost => _featurePanel?.ContentHostPanel ?? DetailContentHost;

    private void ShowPanelProgress(string text)
    {
        PanelProgressBar.Visibility = Visibility.Visible;
        PanelProgressBar.IsIndeterminate = true;
        PanelProgressText.Visibility = Visibility.Visible;
        PanelProgressText.Text = text;
        MoveProgressToTopOfContentHost();
    }

    private void HidePanelProgress()
    {
        PanelProgressBar.Visibility = Visibility.Collapsed;
        PanelProgressText.Visibility = Visibility.Collapsed;
    }

    private void MoveProgressToTopOfContentHost()
    {
        var host = ActiveContentHost;
        if (!host.Children.Contains(PanelProgressBar)) return;
        host.Children.Remove(PanelProgressBar);
        host.Children.Remove(PanelProgressText);
        host.Children.Insert(0, PanelProgressBar);
        host.Children.Insert(1, PanelProgressText);
    }

    private UIElement BuildOptionRow(string label, string? hint, ImageSource? icon, CheckBox cb)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        cb.Content = BuildCheckboxContent(label, icon);
        stack.Children.Add(cb);
        if (!string.IsNullOrWhiteSpace(hint))
        {
            stack.Children.Add(new TextBlock
            {
                Text = hint,
                Margin = new Thickness(26, 2, 0, 0),
                FontSize = 13,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0x5C, 0x54)),
                TextWrapping = TextWrapping.Wrap
            });
        }

        return stack;
    }

    private void ShowOptions(IEnumerable<(string Id, string Label, string Hint, bool Default)> options)
    {
        OptionsPanel.Visibility = Visibility.Visible;
        OptionsPanel.Children.Clear();
        _optionChecks.Clear();
        _itemChecks.Clear();
        foreach (var opt in options)
        {
            var cb = new CheckBox
            {
                IsChecked = opt.Default,
                Tag = opt.Id,
                FontSize = 14
            };
            _optionChecks[opt.Id] = cb;
            OptionsPanel.Children.Add(BuildOptionRow(opt.Label, opt.Hint, null, cb));
        }
    }

    private void ShowItemCheckboxes(IEnumerable<(string Id, string Label, string Hint, bool Default, object Tag, ImageSource? Icon)> items)
    {
        OptionsPanel.Visibility = Visibility.Visible;
        OptionsPanel.Children.Clear();
        _itemChecks.Clear();
        foreach (var item in items)
        {
            var cb = new CheckBox
            {
                IsChecked = item.Default,
                Tag = item.Tag,
                FontSize = 14
            };
            _itemChecks[item.Id] = cb;
            OptionsPanel.Children.Add(BuildOptionRow(item.Label, item.Hint, item.Icon, cb));
        }
    }

    private IEnumerable<T> CheckedItemTags<T>()
    {
        foreach (var pair in _itemChecks)
        {
            if (pair.Value.IsChecked == true && pair.Value.Tag is T tag)
            {
                yield return tag;
            }
        }
    }

    private IEnumerable<string> SelectedOptionIds() =>
        _optionChecks.Where(p => p.Value.IsChecked == true).Select(p => p.Key);

    private void SetDetailGridColumns(string c1, string c2, string c3, double w1 = 200, double w2 = 280, double w3 = 180)
    {
        if (DetailGrid.View is not GridView view || view.Columns.Count < 3) return;
        view.Columns[0].Header = c1;
        view.Columns[1].Header = c2;
        view.Columns[2].Header = c3;
        view.Columns[0].Width = w1;
        view.Columns[1].Width = w2;
        view.Columns[2].Width = w3;
    }

    private void ShowDetailRows(IEnumerable<DetailRow> rows)
    {
        DetailGrid.Visibility = Visibility.Visible;
        DetailGrid.ItemsSource = rows.ToList();
    }

    private void OnCleanClick(object sender, RoutedEventArgs e) => SafeTile(() =>
    {
        _mode = "clean";
        ResetPanelChrome();
        PanelTitle.Text = "Speicher bereinigen";
        PrimaryAction.Content = "Auswahl löschen";
        SecondaryAction.Content = "Vorschau aktualisieren";
        SecondaryAction.Visibility = Visibility.Visible;
        PanelBody.Text = "Wählen Sie, was gelöscht wird. Papierkorb ist standardmäßig aktiviert.";
        _ = ShowCleanupPreviewAsync();
    });

    private void OnSpeedClick(object sender, RoutedEventArgs e) => SafeTile(() =>
    {
        _mode = "speed";
        ResetPanelChrome();
        PanelTitle.Text = "Windows beschleunigen";
        var includeLaptopPower = LaptopPowerService.SupportsExtendedPowerOptions();
        PrimaryAction.Content = "Auswahl anwenden";
        SpeedInputPanel.Visibility = Visibility.Visible;
        var (acMinutes, dcMinutes) = SpeedService.GetCurrentScreenTimeouts();
        ScreenTimeoutAcBox.Text = acMinutes?.ToString() ?? "";
        ScreenTimeoutDcBox.Text = dcMinutes?.ToString() ?? "";
        var timeoutSummary = acMinutes is int ac && dcMinutes is int dc
            ? $"Bildschirm-Timeout: Netzteil {FormatTimeout(ac)} · Akku {FormatTimeout(dc)}."
            : acMinutes is int acOnly
                ? $"Bildschirm-Timeout: Netzteil {FormatTimeout(acOnly)}."
                : dcMinutes is int dcOnly
                    ? $"Bildschirm-Timeout: Akku {FormatTimeout(dcOnly)}."
                    : "";
        if (includeLaptopPower)
        {
            PanelBody.Text =
                "Energieplan, Akku-Lademodus, Kühlprofil, Bildschirm, DNS und Animationen — alles wählbar.\n" +
                timeoutSummary;
        }
        else if (!string.IsNullOrWhiteSpace(timeoutSummary))
        {
            PanelBody.Text = "Energieplan und weitere Optionen — nur auswählen, was Sie wirklich möchten.\n" + timeoutSummary;
        }
        ShowSpeedOptions(includeLaptopPower);
    });

    private static string FormatTimeout(int minutes) =>
        minutes == 0 ? "nie aus" : $"{minutes} Min.";

    private void UpdateTileGridColumns()
    {
        if (TileGrid is null) return;

        var width = ActualWidth;
        TileGrid.Columns = width switch
        {
            < 900 => 1,
            < 1200 => 2,
            < 1500 => 3,
            < 1900 => 4,
            _ => 5
        };
    }

    private void UpdateTileTemperatureChart(LivePerformanceSample live)
    {
        var sensor = live.Temperatures.FirstOrDefault(t => t.Celsius is double && !string.IsNullOrWhiteSpace(t.Id));
        if (sensor?.Celsius is not double celsius)
        {
            TempLabel.Text = "—";
            TilePerfTempChart.Children.Clear();
            return;
        }

        TempLabel.Text = $"{celsius:0} °C";
        var isGpu = TemperatureSparklineRenderer.IsGpuSensor(sensor.Name);
        var history = _performanceMonitor.TempHistoryFor(sensor.Id);
        if (history.Count >= 2)
        {
            TemperatureSparklineRenderer.Draw(TilePerfTempChart, history.Snapshot(), celsius, isGpu);
        }
    }

    private void EnsurePerfDetailCharts()
    {
        if (_detailCpuChart is not null) return;

        PerfDetailPanel.Children.Clear();
        _detailTempRows.Clear();
        _detailHealthSection = null;
        EnsureHealthSection();
        EnsureTrendSection();
        AddPerfDetailChartRow("CPU-Auslastung", "#2F6A4A", 100, out _detailCpuValue, out _detailCpuChart);
        AddPerfDetailChartRow("RAM-Auslastung", "#2F6A4A", 100, out _detailRamValue, out _detailRamChart);
        AddPerfDetailDiskSection(out _detailDiskValue, out _detailDiskChart);

        _detailTempSection = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        _detailTempSection.Children.Add(new TextBlock
        {
            Text = "Temperatur",
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x18, 0x24, 0x1F))
        });
        _detailTempUnavailable = new TextBlock
        {
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x5C, 0x54)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Text = TemperatureService.FormatUnavailableMessage(LibreHardwareTemperatureReader.IsPawnIoInstalled)
        };
        _detailTempSection.Children.Add(_detailTempUnavailable);
        _detailTempHint = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8C, 0x62, 0x39)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };
        _detailTempSection.Children.Add(_detailTempHint);
        _detailTempDiagBlock = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x5C, 0x54)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed
        };
        _detailTempSection.Children.Add(_detailTempDiagBlock);
        PerfDetailPanel.Children.Add(_detailTempSection);
    }

    private void AddPerfDetailChartRow(
        string title,
        string colorHex,
        double maxValue,
        out TextBlock valueBlock,
        out Canvas chartCanvas,
        double? minValue = null,
        double barHeight = 64)
    {
        _ = colorHex;
        _ = maxValue;
        _ = minValue;

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var header = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x18, 0x24, 0x1F)),
            VerticalAlignment = VerticalAlignment.Center
        });
        valueBlock = new TextBlock
        {
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x5C, 0x54)),
            HorizontalAlignment = HorizontalAlignment.Right,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        Grid.SetColumn(valueBlock, 1);
        header.Children.Add(valueBlock);
        chartCanvas = new Canvas { Height = barHeight, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(header);
        panel.Children.Add(chartCanvas);
        PerfDetailPanel.Children.Add(panel);
    }

    private void AddPerfDetailDiskSection(out TextBlock valueBlock, out Canvas chartCanvas)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        panel.Children.Add(new TextBlock
        {
            Text = "Speicher-Belegung",
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x18, 0x24, 0x1F))
        });
        _detailDiskVolumeBox = new ComboBox
        {
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 14,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _detailDiskVolumeBox.SelectionChanged += OnDetailDiskVolumeChanged;
        panel.Children.Add(_detailDiskVolumeBox);
        valueBlock = new TextBlock
        {
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x5C, 0x54)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        panel.Children.Add(valueBlock);
        chartCanvas = new Canvas { Height = 36, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(chartCanvas);
        PerfDetailPanel.Children.Add(panel);
        RefreshDetailDiskVolumeList();
    }

    private void RefreshDetailDiskVolumeList()
    {
        if (_detailDiskVolumeBox is null) return;

        var volumes = VolumeInfoService.ListVolumes();
        var previous = _selectedDiskLetter;
        _detailDiskRefreshing = true;
        _detailDiskVolumeBox.Items.Clear();
        foreach (var volume in volumes)
        {
            _detailDiskVolumeBox.Items.Add(new ComboBoxItem
            {
                Content = VolumeInfoService.FormatComboLabel(volume),
                Tag = volume
            });
        }

        if (_detailDiskVolumeBox.Items.Count == 0)
        {
            _detailDiskRefreshing = false;
            return;
        }

        var selectIndex = 0;
        if (previous is char letter)
        {
            for (var i = 0; i < _detailDiskVolumeBox.Items.Count; i++)
            {
                if (_detailDiskVolumeBox.Items[i] is ComboBoxItem item
                    && item.Tag is VolumeInfo info
                    && info.Letter == letter)
                {
                    selectIndex = i;
                    break;
                }
            }
        }
        else
        {
            var preferred = VolumeInfoService.DefaultSystemVolume(volumes);
            if (preferred is not null)
            {
                for (var i = 0; i < _detailDiskVolumeBox.Items.Count; i++)
                {
                    if (_detailDiskVolumeBox.Items[i] is ComboBoxItem item
                        && item.Tag is VolumeInfo info
                        && info.Letter == preferred.Letter)
                    {
                        selectIndex = i;
                        break;
                    }
                }
            }
        }

        _detailDiskVolumeBox.SelectedIndex = selectIndex;
        _detailDiskRefreshing = false;
        UpdateDiskDetailFromSelection();
    }

    private void UpdateDiskVolumeEntries(IReadOnlyList<VolumeInfo> volumes)
    {
        if (_detailDiskVolumeBox is null) return;

        foreach (var item in _detailDiskVolumeBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is not VolumeInfo old) continue;
            var fresh = volumes.FirstOrDefault(v => v.Letter == old.Letter);
            if (fresh is null) continue;
            item.Content = VolumeInfoService.FormatComboLabel(fresh);
            item.Tag = fresh;
        }
    }

    private void OnDetailDiskVolumeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_detailDiskRefreshing) return;
        UpdateDiskDetailFromSelection();
    }

    private void UpdateDiskDetailFromSelection()
    {
        if (_detailDiskValue is null || _detailDiskChart is null) return;
        if (_detailDiskVolumeBox?.SelectedItem is not ComboBoxItem item || item.Tag is not VolumeInfo volume)
        {
            _detailDiskValue.Text = "Kein Laufwerk verfügbar.";
            _detailDiskChart.Children.Clear();
            return;
        }

        _selectedDiskLetter = volume.Letter;
        if (!volume.IsReady || volume.TotalBytes <= 0)
        {
            _detailDiskValue.Text = $"{volume.Letter}: {VolumeInfoService.KindLabel(volume.DriveType)} — nicht bereit oder leer.";
            _detailDiskChart.Children.Clear();
            return;
        }

        var usedBytes = Math.Max(0, volume.TotalBytes - volume.FreeBytes);
        _detailDiskValue.Text = DiskUsageBarRenderer.FormatLegend(
            $"{volume.Letter}:", usedBytes, volume.FreeBytes, volume.TotalBytes);
        DiskUsageBarRenderer.Draw(_detailDiskChart, usedBytes, volume.TotalBytes);
    }

    private void UpdatePerfDetailCharts(LivePerformanceSample live)
    {
        if (_detailCpuChart is null) return;

        var perf = live.Snapshot;
        _detailCpuValue!.Text = $"{perf.CpuPercent:0} %";
        _detailRamValue!.Text = $"{perf.RamPercent:0} %";
        if (_detailDiskVolumeBox is not null)
        {
            var volumes = VolumeInfoService.ListVolumes();
            if (volumes.Count != _lastVolumeCount)
            {
                _lastVolumeCount = volumes.Count;
                RefreshDetailDiskVolumeList();
            }
            else
            {
                UpdateDiskVolumeEntries(volumes);
                UpdateDiskDetailFromSelection();
            }
        }

        SparklineRenderer.Draw(_detailCpuChart, _performanceMonitor.CpuHistory.Snapshot(), 100,
            Color.FromRgb(0x2F, 0x6A, 0x4A));
        SparklineRenderer.DrawAutoScale(_detailRamChart!, _performanceMonitor.RamHistory.Snapshot(),
            Color.FromRgb(0x2F, 0x6A, 0x4A));
        UpdateDetailTemperatureRows(live);
        UpdateHealthSection(live);
        UpdateTrendSection();
    }

    private void EnsureTrendSection()
    {
        if (_detailTrendSection is not null) return;

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        panel.Children.Add(new TextBlock
        {
            Text = "Verlauf (24 Stunden)",
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x18, 0x24, 0x1F))
        });
        _detailTrendHint = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x7C, 0x75)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 8),
            Text = "Ein Messpunkt pro Minute — Daten bleiben lokal gespeichert."
        };
        panel.Children.Add(_detailTrendHint);
        _detailTrendCpuChart = AddTrendChartRow(panel, "CPU (24 h)");
        _detailTrendRamChart = AddTrendChartRow(panel, "RAM (24 h)");
        _detailTrendTempChart = AddTrendChartRow(panel, "CPU-Temperatur (24 h)");
        _detailTrendSection = panel;
        PerfDetailPanel.Children.Insert(1, panel);
    }

    private static Canvas AddTrendChartRow(StackPanel parent, string title)
    {
        var block = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        block.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x18, 0x24, 0x1F))
        });
        var chart = new Canvas { Height = 48, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        block.Children.Add(chart);
        parent.Children.Add(block);
        return chart;
    }

    private void UpdateTrendSection()
    {
        EnsureTrendSection();
        if (_detailTrendCpuChart is null || _detailTrendRamChart is null || _detailTrendTempChart is null) return;

        var store = _performanceMonitor.TrendStore;
        var cpu = store.CpuSeries();
        var ram = store.RamSeries();
        var temp = store.CpuTempSeries();
        if (_detailTrendHint is not null)
        {
            _detailTrendHint.Text = store.Points.Count switch
            {
                0 => "Verlauf startet — ein Messpunkt pro Minute (max. 24 Stunden).",
                1 => "Erster Messpunkt — Verlauf füllt sich über die nächsten Minuten.",
                _ => $"Verlauf: {store.Points.Count} Messpunkte (max. 24 h, lokal gespeichert)."
            };
        }

        if (cpu.Count >= 2)
        {
            SparklineRenderer.Draw(_detailTrendCpuChart, cpu, 100, Color.FromRgb(0x2F, 0x6A, 0x4A));
        }

        if (ram.Count >= 2)
        {
            SparklineRenderer.DrawAutoScale(_detailTrendRamChart, ram, Color.FromRgb(0x2F, 0x6A, 0x4A));
        }

        if (temp.Count >= 2)
        {
            TemperatureSparklineRenderer.Draw(_detailTrendTempChart, temp, temp[^1], isGpu: false);
        }
    }

    private void EnsureHealthSection()
    {
        if (_detailHealthSection is not null) return;

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        panel.Children.Add(new TextBlock
        {
            Text = "System-Gesundheit",
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x18, 0x24, 0x1F))
        });

        _detailHealthOverall = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        panel.Children.Add(_detailHealthOverall);

        _detailHealthCategories = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(_detailHealthCategories);

        _detailHealthHints = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(_detailHealthHints);

        _detailHealthDisclaimer = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x7C, 0x75)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        };
        panel.Children.Add(_detailHealthDisclaimer);

        _detailHealthSection = panel;
        PerfDetailPanel.Children.Insert(0, panel);
    }

    private void UpdateHealthSection(LivePerformanceSample live)
    {
        EnsureHealthSection();
        if (_detailHealthOverall is null || _detailHealthCategories is null || _detailHealthHints is null
            || _detailHealthDisclaimer is null)
        {
            return;
        }

        var report = SystemHealthService.Evaluate(
            live,
            _healthTracker,
            ShortcutOverlayService.IsAdministrator());

        _detailHealthOverall.Text = $"● {TemperatureZones.OverallLabel(report.Overall)} — {report.OverallSummary}";
        _detailHealthOverall.Foreground = new SolidColorBrush(TemperatureZones.LevelColor(report.Overall));

        _detailHealthCategories.Children.Clear();
        foreach (var category in report.Categories)
        {
            _detailHealthCategories.Children.Add(HealthBarRenderer.CreateCategoryRow(category));
            _detailHealthCategories.Children.Add(new TextBlock
            {
                Text = category.Summary,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x5C, 0x54)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            });
        }

        _detailHealthHints.Children.Clear();
        foreach (var hint in report.Hints)
        {
            var hintPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            hintPanel.Children.Add(new TextBlock
            {
                Text = hint.Title,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(TemperatureZones.LevelColor(hint.Level)),
                TextWrapping = TextWrapping.Wrap
            });
            hintPanel.Children.Add(new TextBlock
            {
                Text = "→ " + hint.Message,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x5C, 0x54)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
            _detailHealthHints.Children.Add(hintPanel);
        }

        _detailHealthDisclaimer.Text = report.Disclaimer;
    }

    private void UpdateTileHealthSummary(LivePerformanceSample live)
    {
        var report = SystemHealthService.Evaluate(
            live,
            _healthTracker,
            ShortcutOverlayService.IsAdministrator());
        TilePerfHealthLabel.Text = SystemHealthService.FormatTileSummary(report);
        TilePerfHealthLabel.Foreground = new SolidColorBrush(TemperatureZones.LevelColor(report.Overall));
    }

    private void UpdateDetailTemperatureRows(LivePerformanceSample live)
    {
        if (_detailTempSection is null) return;

        var sensors = FilterTemperatureSensorsForDisplay(
            live.Temperatures
                .Where(t => t.Celsius is double && !string.IsNullOrWhiteSpace(t.Id))
                .ToList());
        var activeIds = sensors.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var staleId in _detailTempRows.Keys.Where(id => !activeIds.Contains(id)).ToList())
        {
            if (_detailTempRows.TryGetValue(staleId, out var staleRow))
            {
                _detailTempSection.Children.Remove(staleRow.Panel);
                _detailTempRows.Remove(staleId);
            }
        }

        if (_detailTempUnavailable is not null)
        {
            _detailTempUnavailable.Visibility = sensors.Count == 0 && _detailTempRows.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (_detailTempUnavailable.Visibility == Visibility.Visible)
            {
                _detailTempUnavailable.Text = TemperatureService.FormatUnavailableMessage(
                    LibreHardwareTemperatureReader.IsPawnIoInstalled);
            }
        }

        if (_detailTempHint is not null)
        {
            var hint = TemperatureDiagnosticsService.BuildCompactHint(
                live.Temperatures, _performanceMonitor.HardwareTemperatureReader);
            _detailTempHint.Text = hint;
            _detailTempHint.Visibility = string.IsNullOrWhiteSpace(hint)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        foreach (var sensor in sensors)
        {
            if (!_detailTempRows.TryGetValue(sensor.Id, out var row))
            {
                row = CreateTempSensorRow(sensor.Name);
                _detailTempRows[sensor.Id] = row;
                _detailTempSection.Children.Add(row.Panel);
            }

            var isGpu = TemperatureSparklineRenderer.IsGpuSensor(sensor.Name);
            row.Value.Text = $"{sensor.Celsius:0} °C";
            row.Value.Foreground = new SolidColorBrush(
                TemperatureSparklineRenderer.ZoneColor(sensor.Celsius!.Value, isGpu));

            var history = _performanceMonitor.TempHistoryFor(sensor.Id);
            if (history.Count >= 2)
            {
                TemperatureSparklineRenderer.Draw(row.Chart, history.Snapshot(), sensor.Celsius, isGpu);
            }
        }
    }

    private static List<TemperatureSensor> FilterTemperatureSensorsForDisplay(IReadOnlyList<TemperatureSensor> sensors)
    {
        var list = sensors.ToList();
        if (list.Any(s => s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)))
        {
            list = list
                .Where(s => !s.Name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return list;
    }

    private static (TextBlock Value, Canvas Chart, StackPanel Panel) CreateTempSensorRow(string sensorName)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = sensorName,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x18, 0x24, 0x1F)),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 12, 0)
        });
        var valueBlock = new TextBlock
        {
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 72,
            Margin = new Thickness(0, 0, 0, 0),
            TextAlignment = TextAlignment.Right
        };
        Grid.SetColumn(valueBlock, 1);
        header.Children.Add(valueBlock);
        var chart = new Canvas
        {
            Height = 56,
            Margin = new Thickness(0, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ClipToBounds = true
        };
        panel.Children.Add(header);
        panel.Children.Add(chart);
        return (valueBlock, chart, panel);
    }

    private void ShowTemperatureDiagnostics()
    {
        _perfDiagVisible = !_perfDiagVisible;
        SecondaryAction.Content = _perfDiagVisible ? "Diagnose ausblenden" : "Temperatur-Diagnose";

        if (_detailTempDiagBlock is null)
        {
            return;
        }

        if (!_perfDiagVisible)
        {
            _detailTempDiagBlock.Visibility = Visibility.Collapsed;
            return;
        }

        var probes = TemperatureDiagnosticsService.ProbeAll(_performanceMonitor.HardwareTemperatureReader);
        _detailTempDiagBlock.Text = TemperatureDiagnosticsService.FormatReport(
            probes, ShortcutOverlayService.IsAdministrator());
        _detailTempDiagBlock.Visibility = Visibility.Visible;
    }

    private void OnPerfClick(object sender, RoutedEventArgs e) => SafeTile(() =>
    {
        _mode = "perf";
        _perfDiagVisible = false;
        ResetPanelChrome();
        PrimaryAction.Visibility = Visibility.Collapsed;
        SecondaryAction.Content = "Temperatur-Diagnose";
        SecondaryAction.Visibility = Visibility.Visible;
        if (!PawnIoInstallService.IsInstalled)
        {
            TertiaryAction.Content = "PawnIO installieren";
            TertiaryAction.Visibility = Visibility.Visible;
        }

        PanelTitle.Text = "Leistung";
        PanelBody.Visibility = Visibility.Collapsed;
        PerfDetailPanel.Visibility = Visibility.Visible;
        EnsurePerfDetailCharts();
        _lastVolumeCount = VolumeInfoService.ListVolumes().Count;
        if (_detailTempDiagBlock is not null)
        {
            _detailTempDiagBlock.Visibility = Visibility.Collapsed;
        }

        UpdatePerfDetailCharts(_performanceMonitor.Sample());
    });

    private void OnBackupClick(object sender, RoutedEventArgs e) => SafeTile(() =>
    {
        _mode = "backup";
        ResetPanelChrome();
        PanelTitle.Text = "Win Sicherung";
        PanelBody.Text = BuildIsoAwareBody(
            "Sicherung vor größeren Änderungen — Wiederherstellungspunkt oder passende Windows-ISO.\n\n" +
            "• Wiederherstellungspunkt: Schnappschuss von Windows-Einstellungen und Treibern.\n" +
            "• Stick unter Linux (ext4)? Kachel „USB-Stick“ — für Windows umformatieren.\n" +
            "• Windows + Linux auf einem Stick: z. B. Ventoy mit ISO-Dateien.\n\n" +
            (SystemBackupService.IsSystemRestoreEnabled()
                ? "Systemwiederherstellung: aktiv."
                : "Systemwiederherstellung: deaktiviert — unter „Systemschutz“ aktivieren."));
        PrimaryAction.Content = "Wiederherstellungspunkt erstellen";
        SecondaryAction.Content = "ISO automatisch laden";
        SecondaryAction.Visibility = Visibility.Visible;
        TertiaryAction.Content = "Systemschutz öffnen";
        TertiaryAction.Visibility = Visibility.Visible;
    });

    private void OnUsbClick(object sender, RoutedEventArgs e) => SafeTile(() =>
    {
        _mode = "usb";
        ResetPanelChrome();
        PanelTitle.Text = "USB-Stick";
        PanelBody.Text = BuildIsoAwareBody(
            "Wechseldatenträger (USB-Stick/SD) für Windows vorbereiten — z. B. wenn der Stick unter Linux (ext4) formatiert ist.\n\n" +
            "Stick wählen, Dateisystem wählen, dann formatieren. Alle Daten auf dem Stick werden gelöscht.\n\n" +
            "NTFS: Windows-Standard · exFAT: große Sticks / andere Geräte · FAT32: für bootfähige Medien.");
        PrimaryAction.Content = "Stick formatieren";
        SecondaryAction.Content = "Liste aktualisieren";
        SecondaryAction.Visibility = Visibility.Visible;
        TertiaryAction.Content = "ISO automatisch laden";
        TertiaryAction.Visibility = Visibility.Visible;
        UsbFormatPanel.Visibility = Visibility.Visible;
        RefreshUsbDriveList();
    });

    private void OnTransferFoldersClick(object sender, RoutedEventArgs e) => SafeTile(() =>
    {
        _mode = "transfer";
        ResetPanelChrome();
        PanelTitle.Text = "Up-/Download-Ziele";
        _filterUpdating = true;
        try
        {
            FilterBox.Items.Clear();
            FilterBox.Items.Add(new ComboBoxItem { Content = "Zielordner", Tag = "folders" });
            FilterBox.Items.Add(new ComboBoxItem { Content = "Browser-Erweiterung", Tag = "browser" });
            FilterBox.SelectedIndex = 0;
            FilterBox.Visibility = Visibility.Visible;
        }
        finally
        {
            _filterUpdating = false;
        }

        ShowTransferFoldersView();
    });

    private string GetTransferView() =>
        FilterBox.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "folders";

    private void ShowTransferFoldersView()
    {
        PrimaryAction.Content = "Ordner öffnen";
        PrimaryAction.Visibility = Visibility.Visible;
        SecondaryAction.Content = "Ziel hinzufügen";
        SecondaryAction.Visibility = Visibility.Visible;
        TertiaryAction.Content = "Bearbeiten";
        TertiaryAction.Visibility = Visibility.Visible;
        QuaternaryAction.Content = "Schnellzugriff";
        QuaternaryAction.Visibility = Visibility.Visible;
        OptionsPanel.Visibility = Visibility.Collapsed;
        OptionsPanel.Children.Clear();
        RefreshTransferFolderList();
    }

    private void ShowTransferBrowserView()
    {
        PrimaryAction.Content = "Regel hinzufügen";
        PrimaryAction.Visibility = Visibility.Visible;
        SecondaryAction.Content = "Browser einrichten";
        SecondaryAction.Visibility = Visibility.Visible;
        TertiaryAction.Content = "Regel entfernen";
        TertiaryAction.Visibility = Visibility.Visible;
        QuaternaryAction.Content = "Manuell (Entpackt)";
        QuaternaryAction.Visibility = Visibility.Visible;

        OptionsPanel.Visibility = Visibility.Visible;
        OptionsPanel.Children.Clear();
        var enable = new CheckBox
        {
            Content = "Browser-Download-Ziele aktiv",
            IsChecked = _settings.BrowserDownload.Enabled,
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 8)
        };
        enable.Checked += (_, _) =>
        {
            _settings.BrowserDownload.Enabled = true;
            _settings.Save();
            PromptAndInstallBrowserExtension();
            RefreshTransferBrowserList();
        };
        enable.Unchecked += (_, _) =>
        {
            _settings.BrowserDownload.Enabled = false;
            BrowserExtensionInstallService.DisableForAllKnownBrowsers();
            _settings.BrowserDownload.EnabledBrowserIds.Clear();
            _settings.Save();
            RefreshTransferBrowserList();
        };
        OptionsPanel.Children.Add(enable);
        OptionsPanel.Children.Add(new TextBlock
        {
            Text = "Chrome/Edge-Profile sind getrennt (eigene Erweiterungseinstellungen). " +
                   "Google-/Microsoft-Konten im selben Profil können Downloads nicht unterscheiden — dafür das Profil-Kennzeichen in der Erweiterung nutzen.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x5C, 0x54)),
            Margin = new Thickness(0, 0, 0, 8)
        });

        RefreshTransferBrowserList();
    }

    private void RefreshTransferFolderList()
    {
        SetDetailGridColumns("Name", "Pfad", "Status", 160, 400, 80);
        var folders = _settings.TransferFolders;
        if (folders.Count == 0)
        {
            DetailGrid.Visibility = Visibility.Collapsed;
            DetailGrid.ItemsSource = null;
            PanelBody.Text =
                "Noch keine Ziele. „Ziel hinzufügen“ legt einen Ordner an (Name frei wählbar).\n\n" +
                "Vor dem Hochladen in einer Cloud-App den passenden Eintrag öffnen. " +
                "Für automatische Downloads: Registerkarte „Browser-Erweiterung“.";
            return;
        }

        ShowDetailRows(folders.Select(f => new DetailRow
        {
            Col1 = f.Name,
            Col2 = FormatPathForList(f.Path),
            Col3 = TransferFolderService.StatusLabel(f),
            Tag = f
        }));
        PanelBody.Text =
            $"{folders.Count} Ziel(e). „Ordner öffnen“ / „Schnellzugriff“ für manuelle Uploads.\n\n" +
            "Automatische Downloads: Registerkarte „Browser-Erweiterung“.\n" +
            "Platzhalter im Pfad: z. B. …\\Belege\\{yyyy} wechselt am 1.1. automatisch das Jahr.\n" +
            "Lange Pfade in der Liste sind gekürzt. Volle Pfade und Platzhalter unter „Bearbeiten“.";
    }

    private static string FormatPathForList(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        if (!TransferFolderService.ContainsPlaceholders(path))
        {
            try { path = Path.GetFullPath(path); }
            catch { /* keep raw */ }
        }

        if (path.Length <= 58) return path;

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var leaf = Path.GetFileName(trimmed);
        var parentDir = Path.GetDirectoryName(trimmed) ?? "";
        var parent = Path.GetFileName(parentDir);
        var tail = string.IsNullOrEmpty(parent) ? leaf : parent + Path.DirectorySeparatorChar + leaf;
        var root = Path.GetPathRoot(path) ?? "";
        return root + "…" + Path.DirectorySeparatorChar + tail;
    }

    private void RefreshTransferBrowserList()
    {
        SetDetailGridColumns("Website", "Zielordner", "Profil", 220, 220, 120);
        var rules = _settings.BrowserDownload.Rules;
        var folders = _settings.TransferFolders.ToDictionary(f => f.Id, f => f.Name, StringComparer.OrdinalIgnoreCase);

        if (rules.Count == 0)
        {
            DetailGrid.Visibility = Visibility.Collapsed;
            DetailGrid.ItemsSource = null;
        }
        else
        {
            ShowDetailRows(rules.Select(r => new DetailRow
            {
                Col1 = r.HostPattern,
                Col2 = folders.TryGetValue(r.FolderId, out var n) ? n : "(fehlendes Ziel)",
                Col3 = string.IsNullOrWhiteSpace(r.ProfileKey) ? "alle" : r.ProfileKey,
                Tag = r
            }));
        }

        var state = _settings.BrowserDownload.Enabled ? "aktiv" : "aus";
        var browsers = _settings.BrowserDownload.EnabledBrowserIds.Count == 0
            ? "keine Browser"
            : string.Join(", ", _settings.BrowserDownload.EnabledBrowserIds);
        PanelBody.Text =
            $"Browser-Download: {state} · {rules.Count} Regel(n) · {browsers}\n\n" +
            "1) Zielordner anlegen (andere Registerkarte)\n" +
            "2) Regel: Website → Zielordner\n" +
            "3) „Browser einrichten“: installierte Browser wählen — Erweiterung wird per Richtlinie vorinstalliert\n" +
            "4) Browser neu starten\n\n" +
            "Nach dem Download verschiebt KlarWin die Datei in den Zielordner.";
    }

    private TransferFolderTarget? GetSelectedTransferFolder()
    {
        if (DetailGrid.SelectedItem is DetailRow { Tag: TransferFolderTarget tagged })
            return tagged;
        return null;
    }

    private BrowserDownloadRule? GetSelectedBrowserRule()
    {
        if (DetailGrid.SelectedItem is DetailRow { Tag: BrowserDownloadRule tagged })
            return tagged;
        return null;
    }

    private void AddTransferFolderTarget()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Zielordner für Up-/Download wählen"
        };
        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        var path = dialog.FolderName;
        var suggested = TransferFolderService.SuggestNameFromPath(path);
        var name = PromptTransferFolderName(suggested);
        if (string.IsNullOrWhiteSpace(name)) return;

        _settings.TransferFolders.Add(new TransferFolderTarget
        {
            Name = name.Trim(),
            Path = path
        });
        _settings.Save();
        RefreshTransferFolderList();
        PanelBody.Text = $"Ziel „{name.Trim()}“ hinzugefügt.\n\n" +
                         "Vor dem nächsten Upload/Download diesen Eintrag öffnen.";
    }

    private void EditSelectedTransferFolder()
    {
        var target = GetSelectedTransferFolder();
        if (target is null)
        {
            MessageBox.Show(this, "Bitte zuerst ein Ziel in der Liste wählen.", "KlarWin — Up-/Download",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var win = new Window
        {
            Title = "Ziel bearbeiten",
            Width = 680,
            Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.CanResize,
            MinWidth = 560,
            MinHeight = 420
        };
        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(new TextBlock { Text = "Name:", Margin = new Thickness(0, 0, 0, 4) });
        var nameBox = new TextBox { Text = target.Name, Height = 32, Margin = new Thickness(0, 0, 0, 10) };
        stack.Children.Add(nameBox);
        stack.Children.Add(new TextBlock { Text = "Pfad (Vorlage):", Margin = new Thickness(0, 0, 0, 4) });

        var pathFull = new TextBlock
        {
            Text = target.Path,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 4),
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x5C, 0x54))
        };
        stack.Children.Add(pathFull);

        var preview = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0x6B, 0x4F))
        };
        stack.Children.Add(preview);

        var pathRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var pathBox = new TextBox
        {
            Text = target.Path,
            Height = 32,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = target.Path
        };
        void ShowPathStart()
        {
            pathBox.CaretIndex = 0;
            pathBox.SelectionLength = 0;
        }

        void RefreshPreview()
        {
            pathFull.Text = pathBox.Text;
            pathBox.ToolTip = pathBox.Text;
            var raw = pathBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                preview.Text = "";
                return;
            }

            var resolved = TransferFolderService.ResolvePath(raw);
            preview.Text = TransferFolderService.ContainsPlaceholders(raw)
                ? $"Heute → {resolved}"
                : $"Vollpfad → {resolved}";
        }

        pathBox.Loaded += (_, _) =>
        {
            ShowPathStart();
            RefreshPreview();
        };
        pathBox.TextChanged += (_, _) => RefreshPreview();

        var browse = new Button
        {
            Content = "Ordner …",
            Width = 100,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0)
        };
        browse.Click += (_, _) =>
        {
            var current = pathBox.Text.Trim();
            var start = TransferFolderService.ResolvePath(current);
            var dialog = new OpenFolderDialog
            {
                Title = "Zielordner wählen",
                InitialDirectory = Directory.Exists(start) ? start
                    : (Directory.Exists(Path.GetDirectoryName(start) ?? "") ? Path.GetDirectoryName(start)! : "")
            };
            if (dialog.ShowDialog(win) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                pathBox.Text = Path.GetFullPath(dialog.FolderName);
                ShowPathStart();
            }
        };

        var yearToken = new Button
        {
            Content = "→ {yyyy}",
            Width = 90,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Letzten Ordnernamen, wenn er das aktuelle Jahr ist, durch {yyyy} ersetzen"
        };
        yearToken.Click += (_, _) =>
        {
            var raw = pathBox.Text.Trim().TrimEnd('\\', '/');
            if (string.IsNullOrWhiteSpace(raw)) return;
            var leaf = Path.GetFileName(raw);
            var year = DateTime.Now.ToString("yyyy");
            if (leaf.Equals(year, StringComparison.Ordinal)
                || leaf.Equals("{yyyy}", StringComparison.OrdinalIgnoreCase)
                || leaf.Equals("{jahr}", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetDirectoryName(raw) ?? "";
                pathBox.Text = string.IsNullOrEmpty(parent)
                    ? "{yyyy}"
                    : Path.Combine(parent, "{yyyy}");
                ShowPathStart();
                return;
            }

            MessageBox.Show(win,
                $"Der letzte Ordner heißt „{leaf}“, nicht „{year}“.\n\n" +
                "Tipp: Ordner … wählen (z. B. …\\Belege\\2026), dann erneut „→ {{yyyy}}“ — " +
                "oder manuell {{yyyy}} ans Ende schreiben.",
                "KlarWin",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        };

        Grid.SetColumn(pathBox, 0);
        Grid.SetColumn(browse, 1);
        Grid.SetColumn(yearToken, 2);
        pathRow.Children.Add(pathBox);
        pathRow.Children.Add(browse);
        pathRow.Children.Add(yearToken);
        stack.Children.Add(pathRow);

        stack.Children.Add(new TextBlock
        {
            Text = TransferFolderService.PlaceholdersHelpText(),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 12),
            Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x6A, 0x62))
        });

        var buttons = new DockPanel();
        var delete = new Button
        {
            Content = "Löschen",
            Width = 100,
            Height = 34,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        DockPanel.SetDock(delete, Dock.Left);
        delete.Click += (_, _) =>
        {
            var answer = MessageBox.Show(win,
                $"Ziel „{target.Name}“ wirklich entfernen?\n\nDer Ordner auf der Festplatte bleibt erhalten.",
                "KlarWin — Ziel entfernen",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            _settings.TransferFolders.RemoveAll(f => f.Id == target.Id);
            _settings.BrowserDownload.Rules.RemoveAll(r => r.FolderId == target.Id);
            _settings.Save();
            win.DialogResult = true;
            RefreshTransferFolderList();
            PanelBody.Text = $"Ziel „{target.Name}“ entfernt.";
        };

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var save = new Button
        {
            Content = "Speichern",
            Width = 120,
            Height = 34,
            IsDefault = true,
            Margin = new Thickness(0, 0, 8, 0)
        };
        save.Click += (_, _) =>
        {
            var name = nameBox.Text.Trim();
            var path = pathBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show(win, "Bitte einen Namen eingeben.", "KlarWin",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (string.IsNullOrWhiteSpace(path) || !TransferFolderService.DirectoryExists(path))
            {
                MessageBox.Show(win,
                    "Pfad nicht gültig. Bei Platzhaltern muss der übergeordnete Ordner existieren " +
                    "(z. B. …\\Belege bei …\\Belege\\{yyyy}).",
                    "KlarWin",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            target.Name = name;
            target.Path = TransferFolderService.NormalizeStoredPath(path);
            _settings.Save();
            win.DialogResult = true;
            RefreshTransferFolderList();
            var resolved = TransferFolderService.ResolvePath(target.Path);
            PanelBody.Text = $"Ziel „{target.Name}“ aktualisiert.\nVorlage: {target.Path}\nHeute: {resolved}";
        };
        var cancel = new Button { Content = "Abbrechen", Width = 100, Height = 34, IsCancel = true };
        right.Children.Add(save);
        right.Children.Add(cancel);
        buttons.Children.Add(delete);
        buttons.Children.Add(right);
        stack.Children.Add(buttons);
        win.Content = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        win.ShowDialog();
    }

    private void AddBrowserDownloadRule()
    {
        if (_settings.TransferFolders.Count == 0)
        {
            MessageBox.Show(this, "Bitte zuerst mindestens einen Zielordner anlegen.", "KlarWin",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var win = new Window
        {
            Title = "Download-Regel",
            Width = 460,
            Height = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize
        };
        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(new TextBlock { Text = "Website (z. B. lexoffice.de oder shiftbase.com):", Margin = new Thickness(0, 0, 0, 4) });
        var hostBox = new TextBox { Height = 32, Margin = new Thickness(0, 0, 0, 10) };
        stack.Children.Add(hostBox);
        stack.Children.Add(new TextBlock { Text = "Zielordner:", Margin = new Thickness(0, 0, 0, 4) });
        var folderBox = new ComboBox { Height = 32, Margin = new Thickness(0, 0, 0, 10) };
        foreach (var f in _settings.TransferFolders)
            folderBox.Items.Add(new ComboBoxItem { Content = f.Name, Tag = f.Id });
        folderBox.SelectedIndex = 0;
        stack.Children.Add(folderBox);
        stack.Children.Add(new TextBlock
        {
            Text = "Profil-Kennzeichen (optional, muss zur Erweiterung passen):",
            Margin = new Thickness(0, 0, 0, 4)
        });
        var profileBox = new TextBox { Height = 32, Margin = new Thickness(0, 0, 0, 10) };
        stack.Children.Add(profileBox);
        var ok = new Button
        {
            Content = "Speichern",
            Width = 120,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true
        };
        ok.Click += (_, _) =>
        {
            var host = hostBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(host)) return;
            if (folderBox.SelectedItem is not ComboBoxItem { Tag: string folderId }) return;
            host = host.Replace("https://", "", StringComparison.OrdinalIgnoreCase)
                .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
                .Trim().Trim('/');
            if (host.Contains('/'))
                host = host.Split('/')[0];

            _settings.BrowserDownload.Rules.Add(new BrowserDownloadRule
            {
                HostPattern = host,
                FolderId = folderId,
                ProfileKey = profileBox.Text.Trim()
            });
            _settings.BrowserDownload.Enabled = true;
            _settings.Save();
            win.DialogResult = true;
        };
        stack.Children.Add(ok);
        win.Content = stack;
        if (win.ShowDialog() == true)
            RefreshTransferBrowserList();
    }

    private void RemoveSelectedBrowserRule()
    {
        var rule = GetSelectedBrowserRule();
        if (rule is null)
        {
            MessageBox.Show(this, "Bitte eine Regel in der Liste wählen.", "KlarWin",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _settings.BrowserDownload.Rules.RemoveAll(r => r.Id == rule.Id);
        _settings.Save();
        RefreshTransferBrowserList();
    }

    private void PromptAndInstallBrowserExtension()
    {
        var detected = BrowserDetectionService.DetectChromiumBrowsers();
        if (detected.Count == 0)
        {
            MessageBox.Show(this,
                "Kein Chromium-Browser gefunden (Chrome, Edge, Brave).\n\n" +
                "Sie können die Erweiterung weiterhin manuell über „Manuell (Entpackt)“ laden.",
                "KlarWin — Browser",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var win = new Window
        {
            Title = "Browser für KlarWin-Erweiterung",
            Width = 480,
            Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize
        };
        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
            Text = "Welche Browser sollen die KlarWin-Download-Erweiterung automatisch erhalten?\n" +
                   "(Nach der Aktivierung Browser einmal neu starten.)"
        });

        var checks = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
        var previously = _settings.BrowserDownload.EnabledBrowserIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var browser in detected)
        {
            var cb = new CheckBox
            {
                Content = browser.DisplayName,
                IsChecked = previously.Count == 0 || previously.Contains(browser.Id),
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 8),
                Tag = browser
            };
            checks[browser.Id] = cb;
            stack.Children.Add(cb);
        }

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var ok = new Button { Content = "Aktivieren", Width = 120, Height = 34, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) =>
        {
            var selected = checks.Values
                .Where(c => c.IsChecked == true && c.Tag is InstalledBrowser)
                .Select(c => (InstalledBrowser)c.Tag!)
                .ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(win, "Bitte mindestens einen Browser wählen.", "KlarWin",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var (okResult, message) = BrowserExtensionInstallService.EnableForBrowsers(selected);
            _settings.BrowserDownload.Enabled = okResult;
            _settings.BrowserDownload.ExtensionId = BrowserExtensionInstallService.StableExtensionId;
            _settings.BrowserDownload.EnabledBrowserIds = selected.Select(b => b.Id).ToList();
            _settings.Save();
            PanelBody.Text = message;
            MessageBox.Show(win, message, "KlarWin — Erweiterung", MessageBoxButton.OK,
                okResult ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (okResult && message.Contains("neu starten", StringComparison.OrdinalIgnoreCase))
            {
                var restart = MessageBox.Show(win,
                    "Ausgewählte Browser jetzt schließen, damit die Erweiterung geladen wird?\n\n" +
                    "Bitte vorher Arbeit speichern.",
                    "KlarWin",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (restart == MessageBoxResult.Yes)
                    BrowserExtensionInstallService.TryCloseBrowsers(selected);
            }

            win.DialogResult = true;
        };
        var cancel = new Button { Content = "Abbrechen", Width = 100, Height = 34, IsCancel = true };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        stack.Children.Add(row);
        win.Content = stack;
        win.ShowDialog();
        RefreshTransferBrowserList();
    }

    private void SetupBrowserExtensionManual()
    {
        BrowserDownloadBridge.EnsureExtensionFilesCopied();
        var extDir = BrowserDownloadBridge.ExtensionInstallDir;
        try
        {
            File.WriteAllText(
                Path.Combine(extDir, "LIESMICH.txt"),
                "Das ist der KlarWin-Erweiterungsordner.\n" +
                "In Chrome/Edge: Entpackt laden → diesen Ordner wählen (hier liegt manifest.json).\n");
        }
        catch { /* optional */ }

        try { Clipboard.SetText(extDir); } catch { /* clipboard busy */ }
        BrowserDownloadBridge.OpenExtensionFolder();
        BrowserDownloadBridge.RegisterNativeHost(BrowserExtensionInstallService.StableExtensionId);
        _settings.BrowserDownload.ExtensionId = BrowserExtensionInstallService.StableExtensionId;
        _settings.Save();

        MessageBox.Show(this,
            "Manueller Fallback:\n\n" +
            "1. chrome://extensions · Entwicklermodus · Entpackt laden\n" +
            "2. Adresszeile: Strg+V (Pfad kopiert)\n" +
            "3. Ordner „browser-extension“ bestätigen (Dateien sind unsichtbar — nur Unterordner „icons“ erscheint)\n\n" +
            extDir,
            "KlarWin — Manuell laden",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        RefreshTransferBrowserList();
    }
    private string? PromptTransferFolderName(string initial)
    {
        var win = new Window
        {
            Title = "Name für das Ziel",
            Width = 420,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize
        };
        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(new TextBlock
        {
            Text = "Anzeigename (frei wählbar):",
            Margin = new Thickness(0, 0, 0, 6)
        });
        var box = new TextBox { Text = initial, Height = 32 };
        stack.Children.Add(box);
        string? result = null;
        var ok = new Button
        {
            Content = "Speichern",
            Width = 120,
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true
        };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(box.Text)) return;
            result = box.Text.Trim();
            win.DialogResult = true;
        };
        stack.Children.Add(ok);
        win.Content = stack;
        return win.ShowDialog() == true ? result : null;
    }

    private void OpenSelectedTransferFolder()
    {
        var target = GetSelectedTransferFolder();
        if (target is null)
        {
            MessageBox.Show(this, "Bitte zuerst ein Ziel in der Liste wählen.", "KlarWin — Up-/Download",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (ok, message) = TransferFolderService.OpenInExplorer(target.Path);
        PanelBody.Text = message;
        if (!ok) RefreshTransferFolderList();
    }

    private void RemoveSelectedTransferFolder()
    {
        var target = GetSelectedTransferFolder();
        if (target is null)
        {
            MessageBox.Show(this, "Bitte zuerst ein Ziel in der Liste wählen.", "KlarWin — Up-/Download",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            $"Ziel „{target.Name}“ aus der Liste entfernen?\n\nDer Ordner auf der Festplatte bleibt erhalten.",
            "KlarWin — Ziel entfernen",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        _settings.TransferFolders.RemoveAll(f => f.Id == target.Id);
        _settings.BrowserDownload.Rules.RemoveAll(r => r.FolderId == target.Id);
        _settings.Save();
        RefreshTransferFolderList();
    }

    private void PinSelectedTransferFolder()
    {
        var target = GetSelectedTransferFolder();
        if (target is null)
        {
            MessageBox.Show(this, "Bitte zuerst ein Ziel in der Liste wählen.", "KlarWin — Up-/Download",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (ok, message) = TransferFolderService.PinToQuickAccess(target.Path, target.Name);
        PanelBody.Text = message;
        if (!ok) RefreshTransferFolderList();
    }
    private void OnOverlayClick(object sender, RoutedEventArgs e) => SafeTile(() =>
    {
        _mode = "overlay";
        ResetPanelChrome();
        PanelTitle.Text = "Verknüpfungspfeile";
        var current = _overlay.CurrentOverlaySetting();
        var admin = ShortcutOverlayService.IsAdministrator()
            ? "Ja"
            : "Nein — oben rechts „Adminrechte freischalten“ klicken";
        PanelBody.Text =
            $"Aktuell: {current}\n" +
            $"Administrator: {admin}\n\n" +
            "Windows blendet bei Verknüpfungen standardmäßig einen kleinen Pfeil ein. " +
            "Nach Updates kommt er manchmal zurück — KlarWin kann ihn dauerhaft ausblenden.\n\n" +
            "Falls Pfeile nach dem Klick noch sichtbar sind: PC neu starten oder abmelden.";
        PrimaryAction.Content = "Pfeile ausblenden";
        SecondaryAction.Content = "Pfeile wiederherstellen";
        SecondaryAction.Visibility = Visibility.Visible;
    });

    private void OnAutostartClick(object sender, RoutedEventArgs e) => SafeTile(() =>
    {
        _mode = "autostart";
        ResetPanelChrome();
        PanelTitle.Text = "Autostart";
        PrimaryAction.Content = "Auswahl deaktivieren";
        SecondaryAction.Content = "Auswahl aktivieren";
        SecondaryAction.Visibility = Visibility.Visible;
        TertiaryAction.Content = "Programm hinzufügen";
        TertiaryAction.Visibility = Visibility.Visible;
        QuaternaryAction.Content = "Startlayout …";
        QuaternaryAction.Visibility = Visibility.Visible;
        _ = RefreshAutostartListAsync();
    });

    private void OpenStartLayoutPanel()
    {
        _mode = "startlayout";
        ResetPanelChrome();
        PanelTitle.Text = "Startlayout";
        OptionsPanel.Visibility = Visibility.Visible;

        if (!_settings.StartLayout.Enabled)
        {
            PrimaryAction.Content = "Funktion aktivieren";
            SecondaryAction.Visibility = Visibility.Collapsed;
            TertiaryAction.Visibility = Visibility.Collapsed;
            QuaternaryAction.Content = "Zurück";
            QuaternaryAction.Visibility = Visibility.Visible;
            FilterBox.Visibility = Visibility.Collapsed;
            AddStartLayoutDisabledOptions();
            PanelBody.Text =
                "Startlayout ist optional und standardmäßig aus.\n\n" +
                "Wenn Sie die Funktion aktivieren, können Sie Arbeitsplatz-Profile anlegen " +
                "(Monitore + Netzwerk) und Apps bestimmten Monitoren zuordnen.\n\n" +
                "Beim Aktivieren startet KlarWin künftig mit Windows und wendet das passende Layout an.\n\n" +
                "Ohne Aktivierung passiert nichts automatisch.";
            DetailGrid.Visibility = Visibility.Collapsed;
            DetailGrid.ItemsSource = null;
            return;
        }

        PrimaryAction.Content = "Layout anwenden";
        SecondaryAction.Content = "Profil speichern";
        SecondaryAction.Visibility = Visibility.Visible;
        TertiaryAction.Content = "App hinzufügen";
        TertiaryAction.Visibility = Visibility.Visible;
        QuaternaryAction.Content = "Profil-Menü …";
        QuaternaryAction.Visibility = Visibility.Visible;
        FilterBox.Visibility = Visibility.Visible;
        AddStartLayoutOptions();
        RefreshStartLayoutPanel();
    }

    private void AddStartLayoutDisabledOptions()
    {
        OptionsPanel.Children.Clear();
        _optionChecks.Clear();
        OptionsPanel.Children.Add(new TextBlock
        {
            Text = "Erst aktivieren, dann Profile und Apps einrichten.",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x5C, 0x54)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        });
    }

    private void AddStartLayoutOptions()
    {
        OptionsPanel.Children.Clear();
        _optionChecks.Clear();

        var enableCb = new CheckBox
        {
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center
        };
        enableCb.Unchecked += (_, _) =>
        {
            var answer = MessageBox.Show(this,
                "Startlayout deaktivieren?\n\nProfile bleiben gespeichert. KlarWin wird aus dem Windows-Autostart entfernt.",
                "KlarWin — Startlayout",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                enableCb.IsChecked = true;
                return;
            }

            _settings.StartLayout.Enabled = false;
            _settings.StartLayout.ApplyOnLogin = false;
            if (_settings.StartLayout.KlarWinAutostartManaged)
            {
                StartLayoutAutostartHelper.RemoveRegistration();
                _settings.StartLayout.KlarWinAutostartManaged = false;
            }

            _settings.Save();
            OpenStartLayoutPanel();
        };
        OptionsPanel.Children.Add(BuildOptionRow(
            "Startlayout aktiv",
            "Einschalten: Profile nutzen und Layout beim KlarWin-Start automatisch anwenden.",
            null,
            enableCb));
    }

    private void EnableStartLayoutFeature()
    {
        _settings.StartLayout.Enabled = true;
        _settings.StartLayout.ApplyOnLogin = true;
        var autostartMsg = StartLayoutAutostartHelper.EnsureRegistered();
        _settings.StartLayout.KlarWinAutostartManaged = true;
        _settings.Save();
        OpenStartLayoutPanel();
        PanelBody.Text =
            "Startlayout ist aktiviert.\n" +
            autostartMsg + "\n\n" +
            "1) „Profil speichern“ für diesen Arbeitsplatz\n" +
            "2) „App hinzufügen“ (Monitor wählen: z. B. Dell / Samsung / MiniFire)\n" +
            "3) „Layout anwenden“ testen\n\n" +
            StartLayoutService.FormatDiscovery(
                WorkplaceContextService.Capture(), _settings.StartLayout.Profiles);

        MessageBox.Show(this,
            "Startlayout ist aktiv.\n\n" +
            autostartMsg + "\n\n" +
            "Als Nächstes Profil speichern und Apps zuordnen.",
            "KlarWin — Startlayout",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ShowStartLayoutProfileMenu()
    {
        var profile = GetSelectedStartLayoutProfile();
        if (profile is null || FilterBox.SelectedItem is ComboBoxItem { Tag: "" })
        {
            // "(bestes Profil)" — allow refresh only
            var pick = MessageBox.Show(this,
                "Aktuellen Arbeitsplatz neu einlesen?\n\n" +
                "Für Umbenennen/Löschen bitte zuerst ein konkretes Profil in der Liste wählen.",
                "KlarWin — Profil-Menü",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information);
            if (pick == MessageBoxResult.OK) RefreshStartLayoutPanel();
            return;
        }

        var win = new Window
        {
            Title = "Profil-Menü",
            Width = 380,
            Height = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize
        };
        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(new TextBlock
        {
            Text = $"Profil: {profile.Name}",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        });

        void AddBtn(string label, Action action)
        {
            var b = new Button
            {
                Content = label,
                Margin = new Thickness(0, 0, 0, 8),
                Height = 34,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            b.Click += (_, _) =>
            {
                action();
                win.DialogResult = true;
            };
            stack.Children.Add(b);
        }

        AddBtn("Aktualisieren (Monitore neu lesen)", RefreshStartLayoutPanel);
        AddBtn("Profil umbenennen …", () => RenameStartLayoutProfile(profile));
        AddBtn("Gewählte App entfernen", () => RemoveSelectedStartLayoutApp(profile));
        AddBtn("Profil löschen …", () => DeleteStartLayoutProfile(profile));
        win.Content = stack;
        win.ShowDialog();
    }

    private void RenameStartLayoutProfile(WorkplaceProfile profile)
    {
        var win = new Window
        {
            Title = "Profil umbenennen",
            Width = 400,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize
        };
        var stack = new StackPanel { Margin = new Thickness(16) };
        var box = new TextBox { Text = profile.Name, Height = 32 };
        stack.Children.Add(new TextBlock { Text = "Neuer Name:", Margin = new Thickness(0, 0, 0, 6) });
        stack.Children.Add(box);
        var ok = new Button
        {
            Content = "Speichern",
            Width = 120,
            Margin = new Thickness(0, 12, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true
        };
        ok.Click += (_, _) =>
        {
            var name = box.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) return;
            profile.Name = name;
            _settings.Save();
            win.DialogResult = true;
            RefreshStartLayoutPanel();
            PanelBody.Text = $"Profil umbenannt in „{name}“.";
        };
        stack.Children.Add(ok);
        win.Content = stack;
        win.ShowDialog();
    }

    private void DeleteStartLayoutProfile(WorkplaceProfile profile)
    {
        var answer = MessageBox.Show(this,
            $"Profil „{profile.Name}“ und alle zugehörigen Apps wirklich löschen?",
            "KlarWin — Profil löschen",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        _settings.StartLayout.Profiles.RemoveAll(p => p.Id == profile.Id);
        if (_settings.StartLayout.LastProfileId == profile.Id)
            _settings.StartLayout.LastProfileId = null;
        _settings.Save();
        RefreshStartLayoutPanel();
        PanelBody.Text = $"Profil „{profile.Name}“ gelöscht.";
    }

    private void RemoveSelectedStartLayoutApp(WorkplaceProfile profile)
    {
        StartLayoutApp? app = DetailGrid.SelectedItem is DetailRow { Tag: StartLayoutApp tagged }
            ? tagged
            : null;
        if (app is null && DetailGrid.SelectedItem is DetailRow row)
        {
            app = profile.Apps.FirstOrDefault(a =>
                a.Name.Equals(row.Col1, StringComparison.OrdinalIgnoreCase)
                && a.Role.Equals(row.Col2, StringComparison.OrdinalIgnoreCase));
        }

        if (app is null)
        {
            MessageBox.Show(this, "Bitte zuerst eine App in der Liste auswählen (unter „— Apps —“).", "KlarWin",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        profile.Apps.Remove(app);
        _settings.Save();
        RefreshStartLayoutPanel();
        PanelBody.Text = $"App „{app.Name}“ entfernt.";
    }

    private void RefreshStartLayoutPanel()
    {
        if (!_settings.StartLayout.Enabled)
        {
            OpenStartLayoutPanel();
            return;
        }

        var profiles = _settings.StartLayout.Profiles;
        _filterUpdating = true;
        try
        {
            FilterBox.Items.Clear();
            FilterBox.Items.Add(new ComboBoxItem { Content = "(bestes Profil)", Tag = "" });
            foreach (var p in profiles)
            {
                FilterBox.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id });
            }

            var selectedId = _settings.StartLayout.LastProfileId ?? "";
            ComboBoxItem? select = null;
            foreach (ComboBoxItem item in FilterBox.Items)
            {
                if (item.Tag is string id && id == selectedId)
                {
                    select = item;
                    break;
                }
            }

            FilterBox.SelectedItem = select ?? FilterBox.Items[0];
        }
        finally
        {
            _filterUpdating = false;
        }

        ShowStartLayoutDetails();
    }

    private void ShowStartLayoutDetails()
    {
        if (!_settings.StartLayout.Enabled) return;

        var context = WorkplaceContextService.Capture();
        var profiles = _settings.StartLayout.Profiles;
        PanelBody.Text = StartLayoutService.FormatDiscovery(context, profiles);

        SetDetailGridColumns("Position / Modell", "Hersteller · Auflösung", "Serie / Hz", 220, 220, 200);
        DetailGrid.Visibility = Visibility.Visible;

        var active = GetSelectedStartLayoutProfile(context);
        var rows = new List<DetailRow>();
        foreach (var m in context.Monitors.OrderBy(x => x.X))
        {
            var hz = m.RefreshRateHz > 0 ? $"{m.RefreshRateHz} Hz" : "—";
            var serial = !string.IsNullOrWhiteSpace(m.Serial) && m.Serial != "0" ? m.Serial : "—";
            var size = m.DiagonalInches > 0 ? $" · {m.DiagonalInches:0.#}\"" : "";
            rows.Add(new DetailRow
            {
                Col1 = $"[{m.PositionLabel}] {m.Name}",
                Col2 = $"{m.ManufacturerDisplay} · {m.Width}×{m.Height}{size}",
                Col3 = $"{serial} · {hz}",
                Tag = m
            });
        }

        if (active is { Apps.Count: > 0 })
        {
            rows.Add(new DetailRow { Col1 = "— Apps —", Col2 = active.Name, Col3 = $"{active.Apps.Count}" });
            rows.AddRange(active.Apps.Select(a => new DetailRow
            {
                Col1 = a.Name,
                Col2 = a.Role,
                Col3 = a.ExePath,
                Tag = a
            }));
        }

        DetailGrid.ItemsSource = rows;
    }

    private WorkplaceProfile? GetSelectedStartLayoutProfile(WorkplaceContext? context = null)
    {
        context ??= WorkplaceContextService.Capture();
        var profiles = _settings.StartLayout.Profiles;
        if (profiles.Count == 0) return null;

        if (FilterBox.SelectedItem is ComboBoxItem item && item.Tag is string id && !string.IsNullOrEmpty(id))
        {
            return profiles.FirstOrDefault(p => p.Id == id);
        }

        return StartLayoutService.SelectBestProfile(profiles, context);
    }

    private void SaveStartLayoutProfileFromSetup()
    {
        var context = WorkplaceContextService.Capture();
        var name = $"Arbeitsplatz {DateTime.Now:dd.MM. HH:mm}";
        if (_settings.StartLayout.Profiles.Count == 0)
            name = context.Monitors.Count >= 3 ? "Büro" : context.Monitors.Count == 2 ? "Zwei Monitore" : "Nur Laptop";

        var existing = GetSelectedStartLayoutProfile(context);
        if (existing is not null
            && FilterBox.SelectedItem is ComboBoxItem sel
            && sel.Tag is string sid
            && !string.IsNullOrEmpty(sid))
        {
            var answer = MessageBox.Show(this,
                $"Profil „{existing.Name}“ mit aktuellem Monitor-/Netz-Setup aktualisieren?\n\nApps bleiben erhalten.",
                "KlarWin — Profil",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes)
            {
                var updated = StartLayoutService.CreateProfileFromContext(existing.Name, context, existing);
                updated.Id = existing.Id;
                var idx = _settings.StartLayout.Profiles.FindIndex(p => p.Id == existing.Id);
                if (idx >= 0) _settings.StartLayout.Profiles[idx] = updated;
                _settings.StartLayout.LastProfileId = updated.Id;
                _settings.Save();
                PanelBody.Text = $"Profil „{updated.Name}“ aktualisiert.\n\n" +
                                 StartLayoutService.FormatDiscovery(context, _settings.StartLayout.Profiles);
                RefreshStartLayoutPanel();
                return;
            }
        }

        var profile = StartLayoutService.CreateProfileFromContext(name, context);
        _settings.StartLayout.Profiles.Add(profile);
        _settings.StartLayout.LastProfileId = profile.Id;
        _settings.Save();
        RefreshStartLayoutPanel();
        PanelBody.Text = $"Profil „{profile.Name}“ gespeichert.\n\n" +
                         StartLayoutService.FormatDiscovery(context, _settings.StartLayout.Profiles) +
                         "\n\nAls Nächstes „App hinzufügen“ und den Monitor wählen (z. B. Laptop, MiniFire, Samsung).";
    }

    private void AddAppToStartLayoutProfile()
    {
        var context = WorkplaceContextService.Capture();
        var profile = GetSelectedStartLayoutProfile(context);
        if (profile is null)
        {
            MessageBox.Show(this, "Bitte zuerst „Profil speichern“.", "KlarWin — Startlayout",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Migrate legacy primary/left/right → Dell/Samsung/MiniFire style names
        StartLayoutService.SyncRolesToMonitors(profile, context);
        _settings.Save();

        var roles = profile.Roles.Keys.ToList();
        if (roles.Count == 0) roles.Add("Laptop");

        var picked = PickInstalledAppDialog(roles);
        if (picked is null) return;

        profile.Apps.Add(new StartLayoutApp
        {
            Name = picked.Value.App.Name,
            ExePath = picked.Value.App.ExePath,
            Role = picked.Value.Role,
            DelayMs = 1000,
            Maximize = true
        });
        _settings.StartLayout.LastProfileId = profile.Id;
        _settings.Save();
        RefreshStartLayoutPanel();
        PanelBody.Text = $"App hinzugefügt: {picked.Value.App.Name} → {picked.Value.Role}.\n\n" +
                         StartLayoutService.FormatDiscovery(context, _settings.StartLayout.Profiles);
        MessageBox.Show(this,
            $"„{picked.Value.App.Name}“ → Monitor „{picked.Value.Role}“.",
            "KlarWin — App hinzugefügt",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private (InstalledAppEntry App, string Role)? PickInstalledAppDialog(IReadOnlyList<string> roles)
    {
        var win = new Window
        {
            Title = "App wählen",
            Width = 620,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.CanResize
        };

        var filter = new TextBox { Height = 32, Margin = new Thickness(0, 0, 0, 8) };
        var rolePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        rolePanel.Children.Add(new TextBlock
        {
            Text = "Monitor:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        var roleBox = new ComboBox { MinWidth = 200, Height = 32 };
        foreach (var r in roles) roleBox.Items.Add(r);
        roleBox.SelectedIndex = 0;
        rolePanel.Children.Add(roleBox);

        var list = new ListBox { FontSize = 14, DisplayMemberPath = "Display" };
        (InstalledAppEntry App, string Role)? result = null;
        var bottom = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var ok = new Button
        {
            Content = "Hinzufügen",
            Width = 120,
            Height = 34,
            IsDefault = true,
            Margin = new Thickness(0, 0, 8, 0)
        };
        ok.Click += (_, _) =>
        {
            if (list.SelectedItem is not InstalledAppEntry app) return;
            if (roleBox.SelectedItem is not string role) return;
            result = (app, role);
            win.DialogResult = true;
        };
        var cancel = new Button { Content = "Abbrechen", Width = 100, Height = 34, IsCancel = true };
        bottom.Children.Add(ok);
        bottom.Children.Add(cancel);

        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var hint = new TextBlock
        {
            Text = "Programme werden geladen (Startmenü, Desktop, Installationen) …",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x5C, 0x54))
        };
        Grid.SetRow(hint, 0);
        Grid.SetRow(filter, 1);
        Grid.SetRow(rolePanel, 2);
        Grid.SetRow(list, 3);
        Grid.SetRow(bottom, 4);
        grid.Children.Add(hint);
        grid.Children.Add(filter);
        grid.Children.Add(rolePanel);
        grid.Children.Add(list);
        grid.Children.Add(bottom);
        win.Content = grid;

        IReadOnlyList<InstalledAppEntry> apps;
        try
        {
            apps = InstalledAppsCatalogService.ListApps();
        }
        catch
        {
            apps = [];
        }

        hint.Text = apps.Count == 0
            ? "Keine Programme gefunden."
            : $"{apps.Count} Programme gefunden. Tippen zum Filtern, dann Monitor wählen und Hinzufügen.";

        void ApplyFilter()
        {
            var q = filter.Text?.Trim() ?? "";
            list.ItemsSource = string.IsNullOrEmpty(q)
                ? apps
                : apps.Where(a =>
                    a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || a.ExePath.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        filter.TextChanged += (_, _) => ApplyFilter();
        ApplyFilter();

        return win.ShowDialog() == true ? result : null;
    }

    private async Task ApplyStartLayoutAsync()
    {
        if (!_settings.StartLayout.Enabled)
        {
            PanelBody.Text = "Startlayout ist nicht aktiviert.";
            return;
        }

        _settings.StartLayout.ApplyOnLogin = true;
        var autostartMsg = StartLayoutAutostartHelper.EnsureRegistered();
        _settings.StartLayout.KlarWinAutostartManaged = true;
        _settings.Save();

        var context = WorkplaceContextService.Capture();
        var profile = GetSelectedStartLayoutProfile(context);
        if (profile is null)
        {
            MessageBox.Show(this, "Kein Profil vorhanden. Zuerst „Profil speichern“.",
                "KlarWin — Startlayout", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (profile.Apps.Count == 0)
        {
            MessageBox.Show(this, "Dem Profil sind noch keine Apps zugeordnet.\nBitte „App hinzufügen“.",
                "KlarWin — Startlayout", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PrimaryAction.IsEnabled = false;
        ShowPanelProgress("Startlayout wird angewendet …");
        try
        {
            _settings.StartLayout.LastProfileId = profile.Id;
            _settings.Save();
            var log = await StartLayoutService.ApplyAsync(profile, context);
            PanelBody.Text = log + "\n\n" + StartLayoutService.FormatDiscovery(context, _settings.StartLayout.Profiles);
            RefreshStartLayoutPanel();

            MessageBox.Show(this,
                $"Layout „{profile.Name}“ wurde angewendet.\n\n{log}\n\n{autostartMsg}",
                "KlarWin — Layout angewendet",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            PanelBody.Text = UserFriendlyErrors.Translate(ex);
            MessageBox.Show(this, UserFriendlyErrors.Translate(ex), "KlarWin — Fehler",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            HidePanelProgress();
            PrimaryAction.IsEnabled = true;
        }
    }

    private void OnWipeClick(object sender, RoutedEventArgs e) => SafeTile(() =>
    {
        _mode = "wipe";
        ResetPanelChrome();
        WipeModeBox.Visibility = Visibility.Visible;
        PanelTitle.Text = "Sicher löschen";
        PrimaryAction.Content = "② Freien Speicher überschreiben";
        SecondaryAction.Content = "① Datei/Ordner sicher löschen …";
        SecondaryAction.Visibility = Visibility.Visible;

        var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        long free;
        try
        {
            free = ByteSizeFormatter.SanitizeBytes(new DriveInfo(root).AvailableFreeSpace, ByteSizeKind.Disk);
        }
        catch
        {
            free = 0;
        }

        var ssd = false;
        try { ssd = _wipe.LooksLikeSsd(root); } catch { /* WMI optional */ }
        var freeText = ByteSizeFormatter.Format(free, ByteSizeKind.Disk);
        PanelBody.Text =
            "Zwei getrennte Aktionen — bitte nicht verwechseln:\n\n" +
            $"① „Datei/Ordner sicher löschen …“ — Sie wählen eine Datei oder einen Ordner. " +
            "Diese wird überschrieben und danach entfernt.\n\n" +
            $"② „Freien Speicher überschreiben“ — Ihre Dateien und Programme auf {root} bleiben unberührt. " +
            $"Nur der freie Speicher ({freeText}) wird mit Nullen überschrieben, damit bereits gelöschte Reste nicht wiederherstellbar sind." +
            (ssd ? " Auf SSD reicht meist ein Durchlauf." : "");
    });

    private void OnRecoverClick(object sender, RoutedEventArgs e) => SafeTile(() =>
    {
        _mode = "recover";
        ResetPanelChrome();
        PanelTitle.Text = "Dateien retten";
        PrimaryAction.Content = "Auswahl wiederherstellen";
        SecondaryAction.Content = "Liste aktualisieren";
        SecondaryAction.Visibility = Visibility.Visible;
        RefreshRecoverList();
    });

    private void OnKeysClick(object sender, RoutedEventArgs e) => SafeTile(() =>
    {
        _mode = "keys";
        ResetPanelChrome();
        PanelTitle.Text = "Software-Keys sichern";
        PrimaryAction.Content = "Als Textdatei speichern";
        SecondaryAction.Content = "Erneut lesen";
        SecondaryAction.Visibility = Visibility.Visible;
        TertiaryAction.Content = "Als HTML (druckbar)";
        TertiaryAction.Visibility = Visibility.Visible;
        SetDetailGridColumns("Programm", "Product Key", "Art", 220, 420, 140);
        _ = RefreshKeyListAsync();
    });

    private void OnNetworkClick(object sender, RoutedEventArgs e) => SafeTile(() =>
    {
        _mode = "network";
        ResetPanelChrome();
        PanelTitle.Text = "Netzwerk";
        PrimaryAction.Content = "Aktualisieren";
        SecondaryAction.Content = "Router öffnen";
        SecondaryAction.Visibility = Visibility.Visible;
        NetworkNotePanel.Visibility = Visibility.Visible;
        NetworkNoteBox.Text = "";
        NetworkNoteBox.IsEnabled = false;
        NetworkNoteHint.Text = "Gerät in der Liste wählen, dann Bemerkung eintragen (z. B. Handy Chef Sohn).";
        SetDetailGridColumns("Gerät / Bemerkung", "IP-Adresse", "Hersteller / MAC", 220, 120, 260);
        ShowNetworkPanel(_network.Last, refreshList: true);
        _ = ForceNetworkRefreshAsync();
    });

    private void OnAiClick(object sender, RoutedEventArgs e) => SafeTile(() =>
    {
        _mode = "ai";
        ResetPanelChrome();
        FilterBox.Visibility = Visibility.Visible;
        PanelTitle.Text = "KI-Last";
        PrimaryAction.Visibility = Visibility.Collapsed;
        SetDetailGridColumns("Prozess", "CPU", "RAM", 200, 80, 120);
        _ = ShowAiPanelAsync(refreshApps: true);
    });

    private void OnUpdateClick(object sender, RoutedEventArgs e) => SafeTile(() =>
    {
        _mode = "software-updates";
        ResetPanelChrome();
        PanelTitle.Text = "Apps & Treiber aktualisieren";
        PrimaryAction.Content = "Auswahl installieren";
        SecondaryAction.Content = "Liste aktualisieren";
        SecondaryAction.Visibility = Visibility.Visible;
        TertiaryAction.Content = "Alles auswählen";
        TertiaryAction.Visibility = Visibility.Visible;
        PanelBody.Text = "Updates per Checkbox wählen, dann „Auswahl installieren“ — winget bzw. Windows Update startet für die markierten Einträge.";
        _ = RefreshSoftwareUpdatesAsync();
    });

    private void SafeTile(Action action)
    {
        try
        {
            action();
            UpdateActiveTileHighlight();
            OpenFeaturePanel();
        }
        catch (Exception ex)
        {
            PanelTitle.Text = "Fehler";
            PanelBody.Text = UserFriendlyErrors.Translate(ex);
            UpdateActiveTileHighlight();
            OpenFeaturePanel();
        }
    }

    private void OpenFeaturePanel()
    {
        CloseFeaturePanel();

        _featurePanel = new FeaturePanelWindow { Owner = this, Title = $"KlarWin — {PanelTitle.Text}" };
        _featurePanel.Closed += OnFeaturePanelClosed;

        _featurePanel.AttachHeader(PanelTitle);
        _featurePanel.AttachButtons(PanelButtonHost.Children.Cast<UIElement>().ToArray());

        if (PanelBody.Visibility == Visibility.Visible)
        {
            _featurePanel.AttachExtra(PanelBody);
        }
        else
        {
            _featurePanel.AttachExtra(null);
        }

        var content = DetailContentHost.Children.Cast<UIElement>().ToArray();
        _featurePanel.AttachContent(content);
        _featurePanel.Show();
    }

    private void OnFeaturePanelClosed(object? sender, EventArgs e)
    {
        if (sender is not FeaturePanelWindow window) return;
        window.Closed -= OnFeaturePanelClosed;
        if (!ReferenceEquals(window, _featurePanel)) return;
        RestorePanelFromFeatureWindow(window);
        _featurePanel = null;
    }

    private void CloseFeaturePanel()
    {
        if (_featurePanel is null) return;
        var window = _featurePanel;
        _featurePanel = null;
        window.Closed -= OnFeaturePanelClosed;
        RestorePanelFromFeatureWindow(window);
        window.Close();
    }

    private void RestorePanelFromFeatureWindow(FeaturePanelWindow window)
    {
        foreach (var element in window.HeaderStackPanel.Children.Cast<UIElement>().ToList())
        {
            window.HeaderStackPanel.Children.Remove(element);
            PanelHeaderHost.Children.Add(element);
        }

        foreach (var element in window.ButtonHostPanel.Children.Cast<UIElement>().ToList())
        {
            window.ButtonHostPanel.Children.Remove(element);
            ResetButtonHostMargin(element);
            PanelButtonHost.Children.Add(element);
        }

        if (window.ExtraHostBorder.Child is UIElement extra)
        {
            window.ExtraHostBorder.Child = null;
            window.ExtraHostBorder.Visibility = Visibility.Collapsed;
            if (ReferenceEquals(extra, PanelBody))
            {
                if (!PanelChromeHost.Children.Contains(PanelBody))
                {
                    PanelChromeHost.Children.Add(PanelBody);
                }
            }
            else if (!DetailContentHost.Children.Contains(extra))
            {
                DetailContentHost.Children.Insert(0, extra);
            }
        }

        foreach (var element in window.ContentHostPanel.Children.Cast<UIElement>().ToList())
        {
            window.ContentHostPanel.Children.Remove(element);
            if (!DetailContentHost.Children.Contains(element))
            {
                DetailContentHost.Children.Add(element);
            }
        }

        RestoreDetailContentOrder();
    }

    private void RestoreDetailContentOrder()
    {
        UIElement[] order =
        [
            PerfDetailPanel, OptionsPanel, SpeedInputPanel, NetworkNotePanel, UsbFormatPanel,
            PanelProgressBar, PanelProgressText, DetailGrid
        ];
        foreach (var element in order)
        {
            if (DetailContentHost.Children.Contains(element))
            {
                DetailContentHost.Children.Remove(element);
                DetailContentHost.Children.Add(element);
            }
        }
    }

    private void ResetButtonHostMargin(UIElement element)
    {
        if (element is not FrameworkElement fe) return;
        if (ReferenceEquals(element, PrimaryAction))
        {
            fe.Margin = new Thickness(0);
        }
        else if (element is ComboBox)
        {
            fe.Margin = new Thickness(0, 0, 10, 0);
        }
        else if (element is Button)
        {
            fe.Margin = new Thickness(10, 0, 0, 0);
        }
        else
        {
            fe.Margin = new Thickness(0);
        }
    }

    private static string AutostartKey(AutostartEntry entry) =>
        $"{entry.Name}|{entry.Command}|{entry.Location}";

    private void ShowSpeedOptions(bool includeLaptopPower)
    {
        OptionsPanel.Visibility = Visibility.Visible;
        OptionsPanel.Children.Clear();
        _optionChecks.Clear();
        _itemChecks.Clear();

        AddOptionHeader("Energieplan");
        foreach (var option in SpeedService.BuildOptions(includeLaptopPower))
        {
            if (!option.Id.StartsWith("plan-", StringComparison.OrdinalIgnoreCase)) continue;
            AddOptionCheckbox(option.Id, option.Label, option.Hint, option.Default);
        }

        if (includeLaptopPower)
        {
            if (LaptopPowerService.HasBattery())
            {
                AddOptionHeader("Akku — Lademodus");
                AddCurrentSettingNote(LaptopPowerService.FormatCurrentChargingHint());
                foreach (var option in LaptopPowerService.ChargingOptions)
                {
                    var hint = option.Id == LaptopPowerService.GetCurrentChargingModeId()
                        ? option.Hint + " ← aktiv"
                        : option.Hint;
                    AddOptionCheckbox(option.Id, option.Label, hint, false);
                }
            }

            AddOptionHeader("Kühlprofil");
            AddCurrentSettingNote(LaptopPowerService.FormatCurrentThermalHint());
            if (LaptopPowerService.FormatWindowsPowerHint() is { Length: > 0 } powerHint)
            {
                AddCurrentSettingNote(powerHint);
            }

            foreach (var option in LaptopPowerService.ThermalOptions)
            {
                var hint = option.Id == LaptopPowerService.GetCurrentThermalModeId()
                    ? option.Hint + " ← aktiv"
                    : option.Hint;
                AddOptionCheckbox(option.Id, option.Label, hint, false);
            }
        }

        AddOptionHeader("Sonstiges");
        foreach (var option in SpeedService.MiscOptions)
        {
            AddOptionCheckbox(option.Id, option.Label, option.Hint, option.Default);
        }
    }

    private void AddOptionHeader(string text)
    {
        OptionsPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontFamily = new System.Windows.Media.FontFamily("Bahnschrift"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 8),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x18, 0x24, 0x1F))
        });
    }

    private void AddCurrentSettingNote(string text)
    {
        OptionsPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 14,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2F, 0x6A, 0x4A)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
    }

    private void AddOptionCheckbox(string id, string label, string hint, bool defaultChecked)
    {
        var cb = new CheckBox
        {
            IsChecked = defaultChecked,
            Tag = id,
            FontSize = 15
        };
        _optionChecks[id] = cb;
        OptionsPanel.Children.Add(BuildOptionRow(label, hint, null, cb));
    }


    private async Task RefreshAutostartListAsync()
    {
        ShowPanelProgress("Autostart wird geladen …");
        PanelBody.Text = "Autostart-Einträge und Symbole werden gelesen …";
        PrimaryAction.IsEnabled = false;
        try
        {
            var items = await Task.Run(() => _autostart.ListEntries());
            var iconPairs = await Task.WhenAll(items.Select(item => Task.Run(() =>
                (Item: item, Icon: AppIconHelper.LoadSafe(item.IconSource)))));

            var checkItems = iconPairs.Select(pair =>
            (
                AutostartKey(pair.Item),
                $"{pair.Item.Name} — {pair.Item.Description}",
                $"{pair.Item.Location} · {(pair.Item.Enabled ? "Aktiv" : "Deaktiviert")}",
                false,
                (object)pair.Item,
                pair.Icon
            )).ToList();

            _autostartItems = items;
            ShowItemCheckboxes(checkItems);
            PanelBody.Text = _autostartItems.Count == 0
                ? "Keine Autostart-Einträge gefunden."
                : $"{_autostartItems.Count} Einträge — per Checkbox auswählen, dann deaktivieren oder aktivieren.";
        }
        catch (Exception ex)
        {
            PanelBody.Text = UserFriendlyErrors.Translate(ex);
        }
        finally
        {
            HidePanelProgress();
            PrimaryAction.IsEnabled = true;
        }
    }

    private void RefreshAutostartList() => _ = RefreshAutostartListAsync();

    private void AddAutostartProgram()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Programm zum Autostart hinzufügen",
            Filter = "Programme (*.exe)|*.exe|Alle Dateien (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var linkName = Path.GetFileNameWithoutExtension(dialog.FileName) + ".lnk";
        var target = Path.Combine(startup, linkName);
        try
        {
            var script =
                "$ws = New-Object -ComObject WScript.Shell; " +
                $"$s = $ws.CreateShortcut('{target.Replace("'", "''")}'); " +
                $"$s.TargetPath = '{dialog.FileName.Replace("'", "''")}'; " +
                $"$s.WorkingDirectory = '{Path.GetDirectoryName(dialog.FileName)?.Replace("'", "''") ?? ""}'; " +
                "$s.Save()";
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            })?.WaitForExit(8000);
            PanelBody.Text = $"Verknüpfung im Autostart-Ordner angelegt: {linkName}";
            RefreshAutostartList();
        }
        catch (Exception ex)
        {
            PanelBody.Text = UserFriendlyErrors.Translate(ex);
        }
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filterUpdating) return;
        if (_mode == "startlayout")
        {
            if (FilterBox.SelectedItem is ComboBoxItem item && item.Tag is string id)
            {
                _settings.StartLayout.LastProfileId = string.IsNullOrEmpty(id) ? null : id;
                _settings.Save();
            }

            ShowStartLayoutDetails();
            return;
        }

        if (_mode == "transfer")
        {
            if (GetTransferView() == "browser")
                ShowTransferBrowserView();
            else
                ShowTransferFoldersView();
            return;
        }

        if (_mode != "ai") return;
        _aiFilter = FilterBox.SelectedItem is ComboBoxItem aiItem && aiItem.Tag is string tag ? tag : "all";
        _ = ShowAiPanelAsync(refreshApps: false);
    }

    private void OnQuaternaryAction(object sender, RoutedEventArgs e)
    {
        if (_mode == "autostart")
        {
            OpenStartLayoutPanel();
            return;
        }

        if (_mode == "transfer")
        {
            if (GetTransferView() == "browser")
                SetupBrowserExtensionManual();
            else
                PinSelectedTransferFolder();
            return;
        }

        if (_mode == "startlayout")
        {
            if (!_settings.StartLayout.Enabled)
            {
                // Zurück zur Autostart-Liste
                OnAutostartClick(sender, e);
                return;
            }

            ShowStartLayoutProfileMenu();
        }
    }

    private void OnTertiaryAction(object sender, RoutedEventArgs e)
    {
        if (_mode == "perf")
        {
            _ = InstallPawnIoAsync();
            return;
        }

        if (_mode == "backup")
        {
            SystemBackupService.OpenSystemProtectionSettings();
            return;
        }

        if (_mode == "usb")
        {
            _ = DownloadRecommendedWindowsIsoAsync();
            return;
        }

        if (_mode == "autostart")
        {
            AddAutostartProgram();
        }
        else if (_mode == "startlayout")
        {
            if (_settings.StartLayout.Enabled)
                AddAppToStartLayoutProfile();
        }
        else if (_mode == "software-updates")
        {
            foreach (var cb in _itemChecks.Values)
            {
                cb.IsChecked = true;
            }
        }
        else if (_mode == "keys")
        {
            ExportKeysHtml();
        }
        else if (_mode == "transfer")
        {
            if (GetTransferView() == "browser")
                RemoveSelectedBrowserRule();
            else
                EditSelectedTransferFolder();
        }
    }

    private async Task InstallPawnIoAsync()
    {
        if (PawnIoInstallService.IsInstalled)
        {
            MessageBox.Show(this,
                "PawnIO ist bereits installiert.\n\nBitte KlarWin schließen und neu starten, damit CPU/Mainboard-Sensoren gelesen werden können.",
                "KlarWin — PawnIO",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            "PawnIO ist ein kostenloser Hardware-Treiber für CPU- und Mainboard-Temperaturen.\n\n" +
            "KlarWin startet die Installation (winget oder offizieller Installer von GitHub). " +
            "Windows fragt nach Administratorrechten.\n\n" +
            "Fortfahren?",
            "KlarWin — PawnIO installieren",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        SecondaryAction.IsEnabled = false;
        TertiaryAction.IsEnabled = false;
        ShowPanelProgress("PawnIO wird installiert …");

        try
        {
            var progress = new Progress<string>(text => PanelProgressText.Text = text);
            var (success, message) = await PawnIoInstallService.InstallAsync(progress);
            HidePanelProgress();
            MessageBox.Show(this, message, "KlarWin — PawnIO", MessageBoxButton.OK,
                success ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (success && !PawnIoInstallService.IsInstalled)
            {
                TertiaryAction.Content = "PawnIO installieren";
                TertiaryAction.Visibility = Visibility.Visible;
            }
            else if (PawnIoInstallService.IsInstalled)
            {
                TertiaryAction.Visibility = Visibility.Collapsed;
            }

            if (_mode == "perf" && _perfDiagVisible && _detailTempDiagBlock is not null)
            {
                var probes = TemperatureDiagnosticsService.ProbeAll(_performanceMonitor.HardwareTemperatureReader);
                _detailTempDiagBlock.Text = TemperatureDiagnosticsService.FormatReport(
                    probes, ShortcutOverlayService.IsAdministrator());
            }
            else if (_mode == "perf")
            {
                UpdatePerfDetailCharts(_performanceMonitor.Sample());
            }
        }
        catch (Exception ex)
        {
            HidePanelProgress();
            MessageBox.Show(this, UserFriendlyErrors.Translate(ex), "KlarWin — PawnIO",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SecondaryAction.IsEnabled = true;
            TertiaryAction.IsEnabled = true;
        }
    }

    private UsbDriveInfo? GetSelectedUsbDrive()
    {
        return UsbDriveBox.SelectedItem is ComboBoxItem item && item.Tag is UsbDriveInfo drive
            ? drive
            : null;
    }

    private void OnUsbDriveSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_mode != "usb") return;
        var drive = GetSelectedUsbDrive();
        if (drive is null) return;
        ShowUsbDriveDetails(drive);
        RefreshUsbFileList(drive);
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
            case "autostart":
                DisableSelectedAutostart();
                break;
            case "startlayout":
                if (!_settings.StartLayout.Enabled)
                    EnableStartLayoutFeature();
                else
                    await ApplyStartLayoutAsync();
                break;
            case "wipe":
                await RunWipe();
                break;
            case "recover":
                RestoreSelected();
                break;
            case "keys":
                ExportKeys();
                break;
            case "software-updates":
                await InstallSoftwareUpdatesAsync();
                break;
            case "network":
                await ForceNetworkRefreshAsync();
                break;
            case "update":
                await ApplyUpdateAsync();
                break;
            case "backup":
                ApplyRestorePoint();
                break;
            case "usb":
                ApplyUsbFormat();
                break;
            case "transfer":
                if (GetTransferView() == "browser")
                    AddBrowserDownloadRule();
                else
                    OpenSelectedTransferFolder();
                break;
        }
    }

    private void ApplyUsbFormat()
    {
        var drive = GetSelectedUsbDrive();
        if (drive is null)
        {
            PanelBody.Text = "Bitte zuerst ein Wechselmedium in der Liste auswählen.";
            return;
        }

        if (!drive.IsReady)
        {
            PanelBody.Text = $"Laufwerk {drive.Letter}: ist nicht bereit. Stick einstecken und „Liste aktualisieren“ wählen.";
            return;
        }

        var fileSystem = UsbFileSystemBox.SelectedItem is ComboBoxItem fsItem && fsItem.Tag is string fs
            ? fs
            : "NTFS";

        var sizeText = drive.TotalBytes > 0
            ? ByteSizeFormatter.Format(drive.TotalBytes, ByteSizeKind.Disk)
            : "unbekannte Größe";
        var answer = MessageBox.Show(
            this,
            $"Laufwerk {drive.Letter}: ({drive.FileSystem}, {sizeText}) wird komplett gelöscht und als {fileSystem} neu formatiert.\n\n" +
            "Alle Dateien auf dem Stick gehen verloren. Fortfahren?",
            "KlarWin — USB-Stick formatieren",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var (success, message) = UsbStickService.FormatDrive(drive.Letter, fileSystem);
        PanelBody.Text = message;
        RefreshUsbDriveList();
        if (!success && message.Contains("Administrator", StringComparison.OrdinalIgnoreCase))
        {
            PromptAdminElevation("Zum Formatieren eines USB-Sticks sind Administratorrechte nötig.\n\nJetzt neu starten und UAC bestätigen?");
        }
    }

    private void RefreshUsbDriveList()
    {
        var drives = UsbStickService.ListRemovableDrives();
        UsbDriveBox.Items.Clear();
        if (drives.Count == 0)
        {
            DetailGrid.Visibility = Visibility.Collapsed;
            PanelBody.Text =
                "Kein Wechselmedium erkannt (USB-Stick, SD-Karte …). Medium einstecken und „Liste aktualisieren“ wählen.\n\n" +
                "Hinweis: ext4/Linux-Medien erscheinen oft als „RAW / unbekannt“ — Formatierung legt ein Windows-Dateisystem an.";
            return;
        }

        foreach (var drive in drives)
        {
            var size = drive.TotalBytes > 0
                ? ByteSizeFormatter.Format(drive.TotalBytes, ByteSizeKind.Disk)
                : "—";
            var label = string.IsNullOrWhiteSpace(drive.Label) ? "" : $" · {drive.Label}";
            var content = drive.IsReady
                ? $"{drive.Letter}: Wechselmedium{label} · {drive.FileSystem} · {size}"
                : $"{drive.Letter}: Wechselmedium — nicht bereit";
            UsbDriveBox.Items.Add(new ComboBoxItem { Content = content, Tag = drive });
        }

        UsbDriveBox.SelectedIndex = 0;
    }

    private void RefreshUsbFileList(UsbDriveInfo drive)
    {
        if (!drive.IsReady)
        {
            DetailGrid.Visibility = Visibility.Collapsed;
            return;
        }

        DetailGrid.Visibility = Visibility.Visible;
        SetDetailGridColumns("Name", "Typ", "Größe", 240, 120, 140);
        var entries = UsbStickService.ListRootEntries(drive.Letter);
        if (entries.Count == 0)
        {
            ShowDetailRows([
                new DetailRow { Col1 = "(leer)", Col2 = "—", Col3 = "—" }
            ]);
            return;
        }

        ShowDetailRows(entries.Select(entry => new DetailRow
        {
            Col1 = entry.IsDirectory ? $"📁 {entry.Name}" : entry.Name,
            Col2 = entry.IsDirectory ? "Ordner" : "Datei",
            Col3 = entry.IsDirectory ? "—" : ByteSizeFormatter.Format(entry.SizeBytes, ByteSizeKind.Disk)
        }));
    }

    private void ShowUsbDriveDetails(UsbDriveInfo drive)
    {
        var intro =
            "Wechselmedium für Windows vorbereiten — z. B. SD-Karte oder USB-Stick, der unter Linux (ext4) formatiert ist.\n\n" +
            "Medium oben wählen, Dateisystem wählen, dann formatieren. Alle Daten auf dem Medium werden gelöscht.\n\n";

        var summary = UsbStickService.FormatDriveSummary(drive);
        PanelBody.Text = drive.IsReady
            ? intro + summary
            : intro + summary + "\n\nMedium einstecken und „Liste aktualisieren“ wählen.";
    }

    private void OnSecondaryAction(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case "perf":
                ShowTemperatureDiagnostics();
                break;
            case "clean":
                _ = ShowCleanupPreviewAsync();
                break;
            case "overlay":
                ApplyOverlay(hide: false);
                break;
            case "autostart":
                EnableSelectedAutostart();
                break;
            case "startlayout":
                if (_settings.StartLayout.Enabled)
                    SaveStartLayoutProfileFromSetup();
                break;
            case "wipe":
                _ = PickAndSecureDeleteAsync();
                break;
            case "recover":
                RefreshRecoverList();
                break;
            case "keys":
                RefreshKeyList();
                break;
            case "software-updates":
                _ = RefreshSoftwareUpdatesAsync(invalidateCache: true);
                break;
            case "network":
                _network.OpenRouterPage();
                break;
            case "update":
                _ = CheckUpdateAsync(applyPrompt: false);
                break;
            case "backup":
                _ = DownloadRecommendedWindowsIsoAsync();
                break;
            case "usb":
                RefreshUsbDriveList();
                break;
            case "transfer":
                if (GetTransferView() == "browser")
                    PromptAndInstallBrowserExtension();
                else
                    AddTransferFolderTarget();
                break;
        }
    }

    private string BuildIsoAwareBody(string prefix)
    {
        _windowsIsoRecommendation = WindowsIsoRecommendationService.Build(WindowsPcProfileService.Read());
        return prefix + "\n\n" + WindowsIsoRecommendationService.FormatPanelText(_windowsIsoRecommendation);
    }

    private async Task DownloadRecommendedWindowsIsoAsync()
    {
        _windowsIsoRecommendation = WindowsIsoRecommendationService.Build(WindowsPcProfileService.Read());
        PrimaryAction.IsEnabled = false;
        SecondaryAction.IsEnabled = false;
        TertiaryAction.IsEnabled = false;
        ShowPanelProgress("ISO wird vorbereitet …");

        var progress = new Progress<string>(text =>
        {
            PanelProgressText.Text = text;
            PanelProgressText.Visibility = Visibility.Visible;
        });

        try
        {
            var (success, message, path) = await WindowsIsoDownloadService.DownloadIsoAsync(
                _windowsIsoRecommendation, progress);

            PanelBody.Text = BuildIsoAwareBody(
                success ? "Download abgeschlossen." : "Download fehlgeschlagen.") + "\n\n" + message;

            if (success && !string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                    {
                        UseShellExecute = true
                    });
                }
                catch
                {
                    // optional
                }
            }
        }
        catch (Exception ex)
        {
            PanelBody.Text = BuildIsoAwareBody("Download fehlgeschlagen.") + "\n\n" +
                             UserFriendlyErrors.Translate(ex);
        }
        finally
        {
            HidePanelProgress();
            PrimaryAction.IsEnabled = true;
            SecondaryAction.IsEnabled = true;
            TertiaryAction.IsEnabled = true;
        }
    }

    private void ApplyRestorePoint()
    {
        var (success, message) = SystemBackupService.CreateRestorePoint($"KlarWin {UpdateService.CurrentVersion}");
        PanelBody.Text = message;
        if (!success && message.Contains("Administrator", StringComparison.OrdinalIgnoreCase))
        {
            PromptAdminElevation("Für einen Wiederherstellungspunkt sind Administratorrechte nötig.\n\nJetzt neu starten und UAC bestätigen?");
        }
    }

    private async Task ShowCleanupPreviewAsync()
    {
        PanelBody.Text = "Vorschau wird berechnet …";
        var preview = await Task.Run(() => _cleanup.Preview());
        _cleanupPreview = preview;
        ShowOptions(preview.Items.Select(i => (
            i.Id,
            $"{i.Label}: {ByteSizeFormatter.Format(i.Bytes, ByteSizeKind.FolderCleanup)} ({i.Files} {(i.Id == "recycle-bin" ? "Elemente" : "Dateien")})",
            i.Hint,
            i.DefaultSelected)));

        if (preview.Items.Count == 0)
        {
            PanelBody.Text = "Kaum etwas zum Aufräumen gefunden.";
            return;
        }

        var recycle = preview.Items.FirstOrDefault(i => i.Id == "recycle-bin");
        var recycleNote = recycle is { Bytes: 0 }
            ? " Papierkorb ist leer — in Windows-Eigenschaften sehen Sie die reservierte Maximalgröße (z. B. 49 GB auf C:), nicht den aktuellen Inhalt."
            : " Papierkorb ist standardmäßig aktiviert — abwählen, wenn er erhalten bleiben soll.";
        PanelBody.Text = $"Gesamt ca. {ByteSizeFormatter.Format(preview.TotalBytes, ByteSizeKind.FolderCleanup)} in {preview.TotalFiles} Dateien.{recycleNote}";
    }

    private async Task RunCleanup()
    {
        var ids = SelectedOptionIds().ToList();
        if (ids.Count == 0)
        {
            PanelBody.Text = "Bitte mindestens eine Kategorie auswählen.";
            return;
        }

        var selection = CleanupSelection.FromIds(ids);
        var summary = string.Join(", ", ids);
        var confirm = MessageBox.Show(
            this,
            $"Wirklich löschen?\n\n{summary}\n\nDateien in Benutzung werden übersprungen.",
            "KlarWin — Bereinigen",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        PrimaryAction.IsEnabled = false;
        PanelBody.Text = "Aufräumen läuft …";
        var progress = new Progress<string>(text => PanelBody.Text = text);
        var result = await Task.Run(() => _cleanup.CleanNow(selection, progress));
        var extra = result.Notes.Count > 0 ? " " + string.Join(" ", result.Notes) : "";
        PanelBody.Text = $"{result.FilesRemoved} Dateien entfernt, {ByteSizeFormatter.Format(result.BytesRemoved)} frei.{extra}";
        PrimaryAction.IsEnabled = true;
        _ = ShowCleanupPreviewAsync();
    }

    private void RunSpeed()
    {
        var ids = SelectedOptionIds().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0)
        {
            PanelBody.Text = "Bitte mindestens eine Option auswählen.";
            return;
        }

        var planGuids = ids.Where(id => id.StartsWith("plan-", StringComparison.OrdinalIgnoreCase)).ToList();
        if (planGuids.Count > 1)
        {
            PanelBody.Text = "Bitte nur einen Energieplan auswählen.";
            return;
        }

        var dellCharge = ids.Where(id => id.StartsWith("dell-charge-", StringComparison.OrdinalIgnoreCase)).ToList();
        if (dellCharge.Count > 1)
        {
            PanelBody.Text = "Bitte nur einen Lademodus auswählen.";
            return;
        }

        var dellThermal = ids.Where(id => id.StartsWith("dell-thermal-", StringComparison.OrdinalIgnoreCase)).ToList();
        if (dellThermal.Count > 1)
        {
            PanelBody.Text = "Bitte nur ein Kühlprofil auswählen.";
            return;
        }

        int? acMinutes = ParseTimeoutMinutes(ScreenTimeoutAcBox.Text);
        int? dcMinutes = ParseTimeoutMinutes(ScreenTimeoutDcBox.Text);
        if (!string.IsNullOrWhiteSpace(ScreenTimeoutAcBox.Text) && acMinutes is null)
        {
            PanelBody.Text = "Bitte für „Am Netzteil“ eine Zahl von 0 bis 1440 Minuten eintragen (0 = nie aus).";
            return;
        }

        if (!string.IsNullOrWhiteSpace(ScreenTimeoutDcBox.Text) && dcMinutes is null)
        {
            PanelBody.Text = "Bitte für „Am Akku“ eine Zahl von 0 bis 1440 Minuten eintragen (0 = nie aus).";
            return;
        }

        var options = new SpeedOptions
        {
            PowerPlanGuid = planGuids.Count == 1 ? planGuids[0]["plan-".Length..] : null,
            ScreenAcAlwaysOn = ids.Contains("screen-ac-never"),
            ScreenTimeoutAcMinutes = acMinutes,
            ScreenTimeoutDcMinutes = dcMinutes,
            FlushDns = ids.Contains("dns"),
            ReduceAllAnimations = ids.Contains("fx-all"),
            DisableWindowAnimations = ids.Contains("fx-window"),
            DisableTaskbarAnimations = ids.Contains("fx-taskbar"),
            DisablePeek = ids.Contains("fx-peek"),
            DellChargingModes = dellCharge,
            DellThermalModes = dellThermal
        };
        var result = _speed.Apply(options);
        PanelBody.Text = string.Join("\n", result.Notes);
    }

    private static int? ParseTimeoutMinutes(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return int.TryParse(text.Trim(), out var minutes) && minutes >= 0 && minutes <= 1440
            ? minutes
            : null;
    }

    private void ApplyOverlay(bool hide)
    {
        var result = hide ? _overlay.HideArrows() : _overlay.RestoreArrows();
        if (result.NeedsAdmin && !ShortcutOverlayService.IsAdministrator())
        {
            PromptAdminElevation(
                "Für die Desktop-Pfeile sind Administratorrechte nötig.\n\nJetzt neu starten und UAC bestätigen?");
            return;
        }

        if (result.Success)
        {
            _settings.HideShortcutArrows = hide;
            _settings.Save();
        }

        var detail = result.AppliedValue is not null ? $"\nGesetzt: {result.AppliedValue}" : "";
        PanelBody.Text = result.Message + detail + (hide
            ? "\n\nKlarWin merkt sich die Einstellung (beim Start erneut anwenden, wenn als Admin gestartet)."
            : "");
        OnOverlayClick(this, new RoutedEventArgs());
    }

    private void DisableSelectedAutostart()
    {
        var entries = CheckedItemTags<AutostartEntry>().Where(e => e.Enabled).ToList();
        if (entries.Count == 0)
        {
            PanelBody.Text = "Bitte mindestens einen aktiven Eintrag auswählen.";
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"{entries.Count} Programm(e) wirklich aus dem Autostart nehmen?",
            "KlarWin",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var notes = new List<string>();
        foreach (var entry in entries)
        {
            try
            {
                notes.Add(_autostart.Disable(entry));
            }
            catch (Exception ex)
            {
                notes.Add($"{entry.Name}: {UserFriendlyErrors.Translate(ex)}");
            }
        }

        PanelBody.Text = string.Join("\n", notes);
        RefreshAutostartList();
    }

    private void EnableSelectedAutostart()
    {
        var entries = CheckedItemTags<AutostartEntry>().ToList();
        if (entries.Count == 0)
        {
            PanelBody.Text = "Bitte mindestens einen Eintrag auswählen.";
            return;
        }

        var notes = entries.Select(entry =>
        {
            try { return _autostart.Enable(entry); }
            catch (Exception ex) { return $"{entry.Name}: {UserFriendlyErrors.Translate(ex)}"; }
        });
        PanelBody.Text = string.Join("\n", notes);
        RefreshAutostartList();
    }

    private async Task RunWipe()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var mode = SelectedWipeMode();
        var modeLabel = mode switch
        {
            WipeMode.Dod3Pass => "DoD 3 Durchläufe",
            WipeMode.Gutmann35 => "Gutmann 35 Durchläufe",
            _ => "Nullen (1 Durchlauf)"
        };

        var confirm = MessageBox.Show(
            this,
            $"Freier Speicher auf {root} wird mit „{modeLabel}“ überschrieben.\n\n" +
            "Das ist Aktion ② — nicht das Löschen einzelner Dateien.\n" +
            "Vorhandene Dateien und Programme bleiben unberührt.\n" +
            "Nur bereits gelöschte Reste im leeren Speicher werden unbrauchbar." +
            (PanelTitle.Text.Contains("sicher gelöscht", StringComparison.OrdinalIgnoreCase)
                ? "\n\nHinweis: Die soeben gelöschte Datei ist davon nicht betroffen."
                : "") +
            "\n\nDas kann lange dauern. Fortfahren?",
            "Freien Speicher überschreiben — nicht einzelne Dateien",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        if (_wipe.LooksLikeSsd(root) && mode == WipeMode.Gutmann35)
        {
            var ssdAsk = MessageBox.Show(
                this,
                "Auf SSDs bringt Gutmann kaum mehr Sicherheit als einmal Nullen — und braucht extrem lange. Trotzdem Gutmann?",
                "KlarWin",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (ssdAsk != MessageBoxResult.Yes)
            {
                WipeModeBox.SelectedIndex = 0;
                mode = WipeMode.ZeroFill;
            }
        }

        _wipeCts?.Cancel();
        _wipeCts = new CancellationTokenSource();
        PrimaryAction.IsEnabled = false;
        PanelBody.Text = "Überschreiben läuft …";
        var progress = new Progress<string>(text => PanelBody.Text = text);
        var token = _wipeCts.Token;
        var result = await Task.Run(() => _wipe.WipeFreeSpace(root, mode, progress, token), token);
        PanelTitle.Text = "Freier Speicher überschrieben";
        PanelBody.Text = "② " + result.Message + "\n\n① Zum Löschen einzelner Dateien „Datei/Ordner sicher löschen …“ verwenden.";
        PrimaryAction.IsEnabled = true;
    }

    private async Task PickAndSecureDeleteAsync()
    {
        var choice = MessageBox.Show(
            this,
            "Datei löschen (Datei wählen) oder Ordner löschen?",
            "KlarWin",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (choice == MessageBoxResult.Cancel) return;

        string? target = null;
        if (choice == MessageBoxResult.Yes)
        {
            var dialog = new OpenFileDialog { Title = "Datei zum sicheren Löschen wählen", CheckFileExists = true };
            if (dialog.ShowDialog(this) != true) return;
            target = dialog.FileName;
        }
        else
        {
            var folder = new OpenFolderDialog { Title = "Ordner zum sicheren Löschen wählen" };
            if (folder.ShowDialog(this) != true) return;
            target = folder.FolderName;
        }

        var confirm = MessageBox.Show(
            this,
            $"„{Path.GetFileName(target.TrimEnd('\\'))}“ unwiederbringlich löschen?\n\nDer Inhalt wird überschrieben.",
            "KlarWin — Sicher löschen",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        PrimaryAction.IsEnabled = false;
        var mode = SelectedWipeMode();
        var progress = new Progress<string>(text => PanelBody.Text = text);
        var result = await Task.Run(() => _wipe.SecureDeletePath(target, mode, progress));
        PanelTitle.Text = "Datei/Ordner sicher gelöscht";
        PanelBody.Text = "① " + result.Message + "\n\n② Freien Speicher überschreiben betrifft nur den leeren Bereich auf der Festplatte — nicht einzelne Dateien.";
        PrimaryAction.IsEnabled = true;
    }

    private WipeMode SelectedWipeMode()
    {
        if (WipeModeBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return tag switch
            {
                "Dod3Pass" => WipeMode.Dod3Pass,
                "Gutmann35" => WipeMode.Gutmann35,
                _ => WipeMode.ZeroFill
            };
        }

        return WipeMode.ZeroFill;
    }

    private async void RefreshRecoverList()
    {
        PanelBody.Text = "Liste wird geladen …";
        var deep = _itemChecks.TryGetValue("deep-usn", out var deepCb) && deepCb.IsChecked == true;
        _recoverItems = await Task.Run(() =>
        {
            var list = _recovery.ListRecycleBin().ToList();
            if (deep)
            {
                foreach (var hint in _recovery.ScanUsnDeletions())
                {
                    if (list.Any(i => i.OriginalPath.Equals(hint.OriginalPath, StringComparison.OrdinalIgnoreCase))) continue;
                    list.Add(hint);
                }
            }

            return (IReadOnlyList<RecoverableItem>)list.OrderByDescending(i => i.DeletedAt).ToList();
        });

        var checkItems = new List<(string Id, string Label, string Hint, bool Default, object Tag, ImageSource? Icon)>
        {
            ("deep-usn", "NTFS-Protokoll durchsuchen (optional)", "Kürzlich endgültig gelöschte Dateien — nur Hinweis, nicht wiederherstellbar", deep, "deep-usn", null)
        };
        foreach (var item in _recoverItems.Where(i => i.CanRestore))
        {
            var id = "item-" + item.RecyclePath + item.OriginalPath;
            var label = $"{item.DisplayName} ({ByteSizeFormatter.Format(item.SizeBytes)})";
            checkItems.Add((id, label, item.Source + " · " + item.DeletedAt.ToString("dd.MM.yyyy HH:mm"), false, item, null));
        }

        foreach (var item in _recoverItems.Where(i => !i.CanRestore))
        {
            var id = "hint-" + item.OriginalPath;
            var label = $"{item.DisplayName} — nur Hinweis (nicht wiederherstellbar)";
            checkItems.Add((id, label, item.Source + " · " + item.DeletedAt.ToString("dd.MM.yyyy HH:mm"), false, item, null));
        }

        ShowItemCheckboxes(checkItems);

        if (_itemChecks.TryGetValue("deep-usn", out var deepToggle))
        {
            deepToggle.Checked -= OnRecoverDeepToggleChanged;
            deepToggle.Unchecked -= OnRecoverDeepToggleChanged;
            deepToggle.Checked += OnRecoverDeepToggleChanged;
            deepToggle.Unchecked += OnRecoverDeepToggleChanged;
        }

        var restorable = _recoverItems.Count(i => i.CanRestore);
        var hints = _recoverItems.Count - restorable;
        if (_recoverItems.Count == 0)
        {
            PanelBody.Text = deep
                ? "Papierkorb leer. NTFS-Protokoll durchsucht — keine passenden Einträge. Shift+Entf-Hinweise brauchen oft Administratorrechte."
                : "Nichts gefunden. Papierkorb leer — optional NTFS-Protokoll aktivieren und Liste aktualisieren.";
        }
        else
        {
            PanelBody.Text = $"{restorable} aus Papierkorb wiederherstellbar" +
                             (hints > 0 ? $", {hints} Hinweise aus NTFS-Protokoll (nur Anzeige, nicht wiederherstellbar)." : ".") +
                             " — per Checkbox auswählen.";
            if (deep && hints == 0)
            {
                PanelBody.Text += " NTFS-Protokoll: keine zusätzlichen Einträge gefunden.";
            }
        }
    }

    private void OnRecoverDeepToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_mode == "recover")
        {
            RefreshRecoverList();
        }
    }

    private void RestoreSelected()
    {
        var items = CheckedItemTags<RecoverableItem>().Where(i => i.CanRestore).ToList();
        if (items.Count == 0)
        {
            PanelBody.Text = "Bitte mindestens eine wiederherstellbare Datei auswählen (Papierkorb-Einträge).";
            return;
        }

        var notes = items.Select(item => _recovery.Restore(item)).ToList();
        PanelBody.Text = string.Join("\n", notes);
        RefreshRecoverList();
    }

    private async Task RefreshKeyListAsync()
    {
        PanelBody.Text = "Keys werden gelesen …";
        try
        {
            _keyItems = await Task.Run(() => _keys.Collect());
            ShowDetailRows(_keyItems.Select(item => new DetailRow
            {
                Col1 = item.Product,
                Col2 = item.Key,
                Col3 = item.Kind,
                Tag = item
            }));

            PanelBody.Text = _keyItems.Count == 0
                ? "Keine Keys gefunden. Moderne Windows/Office-Lizenzen sind oft digital (Microsoft-Konto) und stehen nicht als klassischer Product Key in der Registry."
                : $"{_keyItems.Count} Keys gefunden. Vollständige Liste — als Text oder HTML (druckbar) unter Dokumente\\KlarWin speichern.";
        }
        catch (Exception ex)
        {
            PanelBody.Text = UserFriendlyErrors.Translate(ex);
        }
    }

    private void RefreshKeyList() => _ = RefreshKeyListAsync();

    private void ExportKeys()
    {
        if (_keyItems.Count == 0)
        {
            RefreshKeyList();
            if (_keyItems.Count == 0)
            {
                PanelBody.Text = "Nichts zu speichern.";
                return;
            }
        }

        var path = _keys.ExportToFile(_keyItems);
        PanelBody.Text = $"Gesichert: {path}";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    private void ExportKeysHtml()
    {
        if (_keyItems.Count == 0)
        {
            RefreshKeyList();
            if (_keyItems.Count == 0)
            {
                PanelBody.Text = "Nichts zu speichern.";
                return;
            }
        }

        var path = _keys.ExportPrintableHtml(_keyItems);
        PanelBody.Text = $"Druckbare HTML-Liste: {path}\nDer Druckdialog wird geöffnet — dort Drucker oder „Als PDF speichern“ wählen.";
        try
        {
            OpenHtmlPrintDialog(path);
        }
        catch (Exception ex)
        {
            PanelBody.Text += $"\n\nDruckdialog konnte nicht geöffnet werden: {UserFriendlyErrors.Translate(ex)}";
        }
    }

    private static void OpenHtmlPrintDialog(string path)
    {
        var fullPath = Path.GetFullPath(path);
        try
        {
            Process.Start(new ProcessStartInfo(fullPath) { Verb = "print", UseShellExecute = true });
            return;
        }
        catch
        {
            // fallback below
        }

        var fileUri = new Uri(fullPath).AbsoluteUri;
        Process.Start(new ProcessStartInfo
        {
            FileName = "rundll32.exe",
            Arguments = $"mshtml.dll,PrintHTML \"{fileUri}\"",
            UseShellExecute = true
        });
    }

    private static string MaskKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return "—";
        if (key.Length <= 8) return key;
        var visible = Math.Min(5, key.Length / 4);
        return key[..visible] + new string('•', Math.Min(12, key.Length - visible - 4)) + key[^4..];
    }

    private async Task ForceNetworkRefreshAsync()
    {
        try
        {
            PanelBody.Text = "Router wird abgefragt …";
            await _network.PingGatewayAsync();
            var snap = await _network.RefreshRouterAsync(includeHosts: true);
            _lastRouterUtc = DateTime.UtcNow;
            _lastHostsUtc = DateTime.UtcNow;
            ApplyNetworkBars(snap);
            ShowNetworkPanel(snap, refreshList: true);
        }
        catch (Exception ex)
        {
            PanelBody.Text = UserFriendlyErrors.Translate(ex);
        }
    }

    private void ShowNetworkPanel(NetworkSnapshot net, bool refreshList)
    {
        var down = net.RouterReachable ? net.WanDownBytesPerSec : net.AdapterDownBytesPerSec;
        var up = net.RouterReachable ? net.WanUpBytesPerSec : net.AdapterUpBytesPerSec;
        var downMax = net.WanDownBitsMax > 0 ? net.WanDownBitsMax : net.AdapterSpeedBits;
        var upMax = net.WanUpBitsMax > 0 ? net.WanUpBitsMax : net.AdapterSpeedBits;
        var lines = new List<string>
        {
            $"{net.AdapterName}: ↓ {NetworkMonitorService.FormatRate(net.AdapterDownBytesPerSec)}  ↑ {NetworkMonitorService.FormatRate(net.AdapterUpBytesPerSec)}"
        };

        if (net.RouterReachable)
        {
            var uptime = net.WanUptime.TotalHours >= 1
                ? $"{net.WanUptime.TotalHours:0} h"
                : $"{net.WanUptime.Minutes} min";
            lines.Add($"{net.RouterName} · {net.WanType} {net.WanStatus}");
            lines.Add($"Leitung max ↓ {NetworkMonitorService.FormatBits(downMax)}  ↑ {NetworkMonitorService.FormatBits(upMax)}");
            if (downMax <= 0)
            {
                lines.Add("Maximalgeschwindigkeit: Router liefert keinen Wert — im Router-Interface unter „Internet“ / „DSL“ nachsehen.");
            }

            lines.Add($"WAN live ↓ {NetworkMonitorService.FormatRate(down)}  ↑ {NetworkMonitorService.FormatRate(up)}");
            lines.Add($"Volumen ↓ {ByteSizeFormatter.Format(net.WanBytesReceived)}  ↑ {ByteSizeFormatter.Format(net.WanBytesSent)}  ·  online {uptime}");
            if (!string.IsNullOrWhiteSpace(net.ExternalIp))
            {
                lines.Add($"Öffentliche IP: {net.ExternalIp}");
            }

            lines.Add($"Geräte: {net.ActiveHostCount} aktiv / {net.HostCount} bekannt");
        }

        if (!string.IsNullOrWhiteSpace(net.Note))
        {
            lines.Add(net.Note);
        }

        lines.Add("Hersteller kommt aus der MAC (OUI), nicht aus dem Gerätenamen. Zufällige MAC = Datenschutz, dann kein Hersteller.");
        lines.Add("Bemerkungen speichern Sie unten — bleiben an der MAC hängen (auch wenn der Name sich ändert).");
        PanelBody.Text = string.Join("\n", lines);

        if (!refreshList) return;
        var rows = net.Hosts.Select(BuildNetworkRow).ToList();
        ShowDetailRows(rows);
        _ = EnrichNetworkVendorsAsync(net.Hosts);
    }

    private DetailRow BuildNetworkRow(RouterHost host)
    {
        var macKey = MacVendorLookup.NormalizeMac(host.Mac);
        var vendorInfo = MacVendorLookup.ResolveLocal(host.Mac, _settings.LearnedMacVendors);
        var note = "";
        if (!string.IsNullOrWhiteSpace(macKey) && _settings.NetworkDeviceNotes.TryGetValue(macKey, out var saved))
        {
            note = saved.Trim();
        }

        var title = string.IsNullOrWhiteSpace(host.Name) || host.Name == "—" ? host.Ip : host.Name;
        if (!string.IsNullOrWhiteSpace(note))
        {
            title = $"{title} — {note}";
        }

        var vendorText = string.IsNullOrWhiteSpace(vendorInfo.Display) ? "Hersteller unbekannt" : vendorInfo.Display;

        return new DetailRow
        {
            Col1 = title,
            Col2 = host.Ip,
            Col3 = $"{vendorText} · {host.Mac} · {host.InterfaceType}",
            Tag = host
        };
    }

    private async Task EnrichNetworkVendorsAsync(IReadOnlyList<RouterHost> hosts)
    {
        if (_mode != "network" || hosts.Count == 0) return;
        var changed = false;
        foreach (var host in hosts.Take(25))
        {
            if (string.IsNullOrWhiteSpace(host.Mac) || MacVendorLookup.IsRandomizedMac(host.Mac)) continue;
            var before = MacVendorLookup.ResolveLocal(host.Mac, _settings.LearnedMacVendors);
            if (!string.IsNullOrWhiteSpace(before.Vendor)) continue;
            var resolved = await MacVendorLookup.ResolveAsync(host.Mac, _settings.LearnedMacVendors);
            if (!string.IsNullOrWhiteSpace(resolved.Vendor))
            {
                changed = true;
            }

            await Task.Delay(350);
        }

        if (changed)
        {
            _settings.Save();
            if (_mode == "network")
            {
                ShowDetailRows(_network.Last.Hosts.Select(BuildNetworkRow));
            }
        }
    }

    private void OnDetailGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_mode != "network") return;
        if (DetailGrid.SelectedItem is not DetailRow row || row.Tag is not RouterHost host)
        {
            NetworkNoteBox.IsEnabled = false;
            NetworkNoteBox.Text = "";
            NetworkNoteHint.Text = "Gerät in der Liste wählen, dann Bemerkung eintragen (z. B. Handy Chef Sohn).";
            return;
        }

        var macKey = MacVendorLookup.NormalizeMac(host.Mac);
        NetworkNoteBox.IsEnabled = !string.IsNullOrWhiteSpace(macKey);
        if (string.IsNullOrWhiteSpace(macKey))
        {
            NetworkNoteHint.Text = "Für dieses Gerät fehlt die MAC — Bemerkung kann nicht gespeichert werden.";
            NetworkNoteBox.Text = "";
            return;
        }

        var label = string.IsNullOrWhiteSpace(host.Name) || host.Name == "—" ? host.Ip : host.Name;
        NetworkNoteHint.Text = $"Bemerkung für „{label}“ ({host.Mac}):";
        NetworkNoteBox.Text = _settings.NetworkDeviceNotes.TryGetValue(macKey, out var note) ? note : "";
    }

    private void OnNetworkNoteSave(object sender, RoutedEventArgs e)
    {
        if (DetailGrid.SelectedItem is not DetailRow row || row.Tag is not RouterHost host)
        {
            PanelBody.Text = "Bitte zuerst ein Gerät in der Liste wählen.";
            return;
        }

        var macKey = MacVendorLookup.NormalizeMac(host.Mac);
        if (string.IsNullOrWhiteSpace(macKey))
        {
            PanelBody.Text = "Ohne MAC-Adresse kann keine Bemerkung gespeichert werden.";
            return;
        }

        var text = NetworkNoteBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            _settings.NetworkDeviceNotes.Remove(macKey);
        }
        else
        {
            _settings.NetworkDeviceNotes[macKey] = text;
        }

        _settings.Save();
        ShowDetailRows(_network.Last.Hosts.Select(BuildNetworkRow));
        PanelBody.Text = string.IsNullOrWhiteSpace(text)
            ? "Bemerkung gelöscht."
            : $"Bemerkung gespeichert: „{text}“";
    }

    private async Task ShowAiPanelAsync(bool refreshApps)
    {
        try
        {
            PanelBody.Text = "KI-Prozesse werden gelesen …";
            var perf = await Task.Run(() => _performanceMonitor.Sample().Snapshot);
            var ai = await Task.Run(() => _ai.Capture(_aiFilter, perf.CpuPercent, perf.RamTotalBytes));
            ShowAiPanel(ai, refreshApps);
        }
        catch (Exception ex)
        {
            PanelBody.Text = UserFriendlyErrors.Translate(ex);
        }
    }

    private void ShowAiPanel(AiLoadSnapshot ai, bool refreshApps)
    {
        if (refreshApps)
        {
            _filterUpdating = true;
            var selected = _aiFilter;
            FilterBox.Items.Clear();
            FilterBox.Items.Add(new ComboBoxItem { Content = "Alle KI", Tag = "all", IsSelected = selected is "all" or "" });
            foreach (var app in ai.AvailableApps)
            {
                FilterBox.Items.Add(new ComboBoxItem
                {
                    Content = app.Label,
                    Tag = app.Id,
                    IsSelected = app.Id.Equals(selected, StringComparison.OrdinalIgnoreCase)
                });
            }

            if (FilterBox.SelectedIndex < 0) FilterBox.SelectedIndex = 0;
            _filterUpdating = false;
        }

        var gpu = ai.GpuAvailable
            ? $"{ai.GpuName}: {ai.GpuPercent:0}% · VRAM {ByteSizeFormatter.Format(ai.GpuMemoryUsedBytes)} / {ByteSizeFormatter.Format(ai.GpuMemoryTotalBytes)}"
            : "Keine NVIDIA-GPU erkannt";
        var onGpu = ai.SelectedOnGpu ? "läuft auf GPU" : "nicht auf GPU";
        PanelBody.Text =
            $"Filter: {ai.FilterLabel}\n" +
            $"KI-Anteil: {ai.CpuPercent:0}% CPU · {ByteSizeFormatter.Format(ai.RamBytes)} RAM ({ai.RamPercent:0}%)\n" +
            $"System gesamt: {ai.SystemCpuPercent:0}% CPU · {onGpu}\n" +
            $"{gpu}\n" +
            "Erkannt über laufende Prozesse (kein Festplatten-Scan).";

        if (ai.Rows.Count == 0)
        {
            ShowDetailRows([new DetailRow { Col1 = "—", Col2 = "Keine KI-Prozesse", Col3 = "Cursor, Ollama, Claude, …" }]);
            return;
        }

        ShowDetailRows(ai.Rows.Select(row =>
        {
            var count = row.Count > 1 ? $" ×{row.Count}" : "";
            return new DetailRow
            {
                Col1 = row.Name + count,
                Col2 = $"{row.CpuPercent:0.0}%",
                Col3 = ByteSizeFormatter.Format(row.RamBytes) + (row.OnGpu ? " · GPU" : ""),
                Tag = row
            };
        }));
    }

    private void UpdateAiLive(AiLoadSnapshot ai)
    {
        var gpu = ai.GpuAvailable
            ? $"{ai.GpuName}: {ai.GpuPercent:0}% · VRAM {ByteSizeFormatter.Format(ai.GpuMemoryUsedBytes)} / {ByteSizeFormatter.Format(ai.GpuMemoryTotalBytes)}"
            : "Keine NVIDIA-GPU erkannt";
        var onGpu = ai.SelectedOnGpu ? "läuft auf GPU" : "nicht auf GPU";
        PanelBody.Text =
            $"Filter: {ai.FilterLabel}\n" +
            $"KI-Anteil: {ai.CpuPercent:0}% CPU · {ByteSizeFormatter.Format(ai.RamBytes)} RAM ({ai.RamPercent:0}%)\n" +
            $"System gesamt: {ai.SystemCpuPercent:0}% CPU · {onGpu}\n" +
            $"{gpu}\n" +
            "Erkannt über laufende Prozesse (kein Festplatten-Scan).";
    }

    private async Task QuietUpdateCheckAsync()
    {
        var info = await _update.CheckAsync();
        if (!info.UpdateAvailable) return;
        VersionLabel.Text = $"Version {info.CurrentVersion} · Update {info.LatestVersion} (Einstellungen → KlarWin Update)";
    }

    private async Task RefreshSoftwareUpdatesAsync(bool invalidateCache = false)
    {
        PrimaryAction.IsEnabled = false;
        ShowPanelProgress("Updates werden gesucht …");
        PanelBody.Text = "Programme (winget) und Treiber (Windows Update) werden parallel geprüft …";
        try
        {
            if (!SoftwareUpdateService.IsWingetAvailable())
            {
                PanelBody.Text = "„winget“ ist nicht verfügbar. Bitte App Installer / Windows Package Manager installieren, dann erneut prüfen.";
            }

            if (invalidateCache)
            {
                SoftwareUpdateService.InvalidateCache();
            }

            var progress = new Progress<string>(t =>
            {
                PanelBody.Text = t;
                PanelProgressText.Text = t;
            });
            var wingetTask = _softwareUpdates.ListWingetOnlyAsync(progress);
            var driverTask = _softwareUpdates.ListDriversOnlyAsync(progress);

            var apps = await wingetTask;
            _softwareUpdateItems = apps.ToList();
            ShowSoftwareUpdateCheckboxes(_softwareUpdateItems);
            PanelBody.Text = apps.Count == 0
                ? "Keine Programm-Updates (winget). Treiber werden geprüft …"
                : $"{apps.Count} Programm-Update(s) — Treiber werden geprüft …";

            var drivers = await driverTask;
            _softwareUpdateItems = apps.Concat(drivers).ToList();
            ShowSoftwareUpdateCheckboxes(_softwareUpdateItems);

            var appCount = _softwareUpdateItems.Count(i => i.Kind == "Programm");
            var driverCount = _softwareUpdateItems.Count(i => i.Kind == "Treiber");
            PanelBody.Text = _softwareUpdateItems.Count == 0
                ? "Keine Updates gefunden. KlarWin selbst aktualisieren Sie unter Einstellungen → KlarWin Update."
                : $"{appCount} Programm(e), {driverCount} Treiber — auswählen und „Auswahl installieren“. KlarWin-Update: Zahnrad → KlarWin Update.";
        }
        catch (Exception ex)
        {
            PanelBody.Text = UserFriendlyErrors.Translate(ex);
        }
        finally
        {
            HidePanelProgress();
            PrimaryAction.IsEnabled = true;
        }
    }

    private void ShowSoftwareUpdateCheckboxes(IReadOnlyList<SoftwareUpdateItem> items)
    {
        ShowItemCheckboxes(items.Select(item =>
            (item.Id + "|" + item.Kind,
            $"{item.Kind}: {item.Name}  ({item.CurrentVersion} → {item.AvailableVersion})",
            item.Source == "winget" ? "winget" : "Windows Update",
            false,
            (object)item,
            (ImageSource?)null)));
    }

    private async Task InstallSoftwareUpdatesAsync()
    {
        var selected = CheckedItemTags<SoftwareUpdateItem>().ToList();
        if (selected.Count == 0)
        {
            PanelBody.Text = "Bitte mindestens ein Update auswählen (oder „Alles auswählen“).";
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"{selected.Count} Update(s) wirklich installieren?\n\nManche Programme starten ggf. neu. Treiber können einen Neustart verlangen.",
            "KlarWin — Updates",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        PrimaryAction.IsEnabled = false;
        try
        {
            var result = await RunWithInstallProgressAsync(selected.Count, async progress =>
            {
                return await _softwareUpdates.InstallSelectedAsync(selected, progress);
            });
            PanelBody.Text = result;
        }
        catch (Exception ex)
        {
            PanelBody.Text = UserFriendlyErrors.Translate(ex);
        }
        finally
        {
            PrimaryAction.IsEnabled = true;
            await RefreshSoftwareUpdatesAsync(invalidateCache: true);
        }
    }

    private async Task CheckUpdateAsync(bool applyPrompt)
    {
        PanelBody.Text = "Update-Server wird gefragt …";
        var info = await _update.CheckAsync();
        _pendingUpdate = info;
        VersionLabel.Text = info.UpdateAvailable
            ? $"Version {info.CurrentVersion} · Update {info.LatestVersion} verfügbar"
            : $"Version {info.CurrentVersion}";
        PanelBody.Text = string.IsNullOrWhiteSpace(info.Notes)
            ? info.Message
            : $"{info.Message} {info.Notes}";
        PrimaryAction.Visibility = info.UpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
        PrimaryAction.Content = "Jetzt aktualisieren";

        if (applyPrompt && info.UpdateAvailable)
        {
            var answer = MessageBox.Show(
                this,
                $"{info.Message}\n\nJetzt herunterladen und neu starten?",
                "KlarWin Update",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
            {
                await ApplyUpdateAsync();
            }
        }
    }

    private async Task ApplyUpdateAsync()
    {
        _pendingUpdate ??= await _update.CheckAsync();
        if (_pendingUpdate is null || !_pendingUpdate.UpdateAvailable)
        {
            await CheckUpdateAsync(applyPrompt: false);
            return;
        }

        PrimaryAction.IsEnabled = false;
        var progress = new Progress<string>(text => PanelBody.Text = text);
        var message = await _update.DownloadAndPrepareRestartAsync(_pendingUpdate, progress);
        if (message == "UPDATE_OK")
        {
            PanelBody.Text = "Update wird installiert — KlarWin startet gleich neu …";
            App.ReleaseSingleInstanceForUpdate();
            await Task.Delay(1500);
            Environment.Exit(0);
            return;
        }

        PanelBody.Text = message;
        PrimaryAction.IsEnabled = true;
    }
}