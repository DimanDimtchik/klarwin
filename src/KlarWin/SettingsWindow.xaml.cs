using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using KlarWin.Services;
using Microsoft.Win32;

namespace KlarWin;

public partial class SettingsWindow : Window
{
    private readonly KlarWinSettings _settings;
    private readonly Action _onApplied;
    private readonly UpdateService _update = new();
    private UpdateInfo? _pendingKlarWin;

    public SettingsWindow(KlarWinSettings settings, Action onApplied)
    {
        _settings = settings;
        _onApplied = onApplied;
        InitializeComponent();
        LoadFonts();
        LoadFromSettings();
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            _ = RefreshKlarWinUpdateAsync();
        };
    }

    private void LoadFonts()
    {
        string[] fonts =
        [
            "Sitka Text", "Bahnschrift", "Segoe UI", "Calibri", "Candara",
            "Georgia", "Verdana", "Trebuchet MS", "Consolas", "Cambria"
        ];
        foreach (var f in fonts)
        {
            FontUiBox.Items.Add(f);
            FontTitleBox.Items.Add(f);
        }
    }

    private void LoadFromSettings()
    {
        BgGradient.IsChecked = _settings.BackgroundMode is "Gradient" or null or "";
        BgSolid.IsChecked = _settings.BackgroundMode == "Solid";
        BgImage.IsChecked = _settings.BackgroundMode == "Image";
        Color1Box.Text = _settings.BackgroundColor1;
        Color2Box.Text = _settings.BackgroundColor2;
        Color3Box.Text = _settings.BackgroundColor3;
        ImagePathBox.Text = _settings.BackgroundImagePath;
        FontUiBox.Text = _settings.FontFamilyUi;
        FontTitleBox.Text = _settings.FontFamilyTitle;
        FontSizeBodyBox.Text = _settings.FontSizeBody.ToString("0");
        FontSizeTitleBox.Text = _settings.FontSizeTitle.ToString("0");
        InkBox.Text = _settings.InkColor;
        MuteBox.Text = _settings.MuteColor;
        AccentBox.Text = _settings.AccentColor;
        TileRadiusBox.Text = _settings.TileCornerRadius.ToString("0");
        TileThicknessBox.Text = _settings.TileBorderThickness.ToString("0.##");
        TileBorderBox.Text = _settings.TileBorderColor;
        TileHoverBox.Text = _settings.TileHoverBorderColor;
        TileBgBox.Text = _settings.TileBackgroundColor;
        SelectPromoPos(_settings.PromoPosition);
        ShowDesktopIconBox.IsChecked = DesktopShortcutService.DesktopShortcutExists() || _settings.ShowDesktopIcon;
        RefreshDesktopStatus();
    }

    private void RefreshDesktopStatus()
    {
        var visible = ShowDesktopIconBox.IsChecked == true;
        DesktopIconStatus.Text = DesktopShortcutService.DesktopShortcutExists()
            ? "Aktuell: KlarWin-Symbol ist auf dem Desktop sichtbar."
            : visible
                ? "Nach „Übernehmen“ wird die Desktop-Verknüpfung angelegt."
                : "Aktuell: kein KlarWin-Symbol auf dem Desktop.";
    }

    private void OnDesktopSettingChanged(object sender, RoutedEventArgs e) => RefreshDesktopStatus();

    private void SelectPromoPos(string pos)
    {
        foreach (ComboBoxItem item in PromoPosBox.Items)
        {
            if (item.Tag is string tag && tag.Equals(pos, StringComparison.OrdinalIgnoreCase))
            {
                PromoPosBox.SelectedItem = item;
                return;
            }
        }

        PromoPosBox.SelectedIndex = 0;
    }

    private void SaveToSettings()
    {
        _settings.BackgroundMode = BgImage.IsChecked == true ? "Image"
            : BgSolid.IsChecked == true ? "Solid"
            : "Gradient";
        _settings.BackgroundColor1 = Color1Box.Text.Trim();
        _settings.BackgroundColor2 = Color2Box.Text.Trim();
        _settings.BackgroundColor3 = Color3Box.Text.Trim();
        _settings.BackgroundImagePath = ImagePathBox.Text.Trim();
        _settings.FontFamilyUi = string.IsNullOrWhiteSpace(FontUiBox.Text) ? "Sitka Text" : FontUiBox.Text.Trim();
        _settings.FontFamilyTitle = string.IsNullOrWhiteSpace(FontTitleBox.Text) ? "Bahnschrift" : FontTitleBox.Text.Trim();
        if (double.TryParse(FontSizeBodyBox.Text, out var body)) _settings.FontSizeBody = body;
        if (double.TryParse(FontSizeTitleBox.Text, out var title)) _settings.FontSizeTitle = title;
        _settings.InkColor = InkBox.Text.Trim();
        _settings.MuteColor = MuteBox.Text.Trim();
        _settings.AccentColor = AccentBox.Text.Trim();
        if (double.TryParse(TileRadiusBox.Text, out var radius)) _settings.TileCornerRadius = radius;
        if (double.TryParse(TileThicknessBox.Text, out var thick)) _settings.TileBorderThickness = thick;
        _settings.TileBorderColor = TileBorderBox.Text.Trim();
        _settings.TileHoverBorderColor = TileHoverBox.Text.Trim();
        _settings.TileBackgroundColor = TileBgBox.Text.Trim();
        _settings.ShowPromoBanner = true;
        _settings.PromoPosition = PromoPosBox.SelectedItem is ComboBoxItem pos && pos.Tag is string tag ? tag : "Bottom";
        _settings.ShowDesktopIcon = ShowDesktopIconBox.IsChecked == true;
        _settings.Save();
    }

    private void ApplyDesktopShortcut()
    {
        try
        {
            DesktopShortcutService.EnsureDesktopShortcut(_settings.ShowDesktopIcon);
            RefreshDesktopStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                UserFriendlyErrors.Translate(ex),
                "KlarWin — Desktop-Symbol",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task RefreshKlarWinUpdateAsync()
    {
        KlarWinUpdateStatus.Text = $"Installierte Version: {UpdateService.CurrentVersion} — prüfe Server …";
        KlarWinUpdateButton.IsEnabled = false;
        try
        {
            _pendingKlarWin = await _update.CheckAsync();
            var running = UpdateService.CurrentVersion;
            var runningPath = Environment.ProcessPath ?? DesktopShortcutService.ResolveInstalledExePath();
            var canonical = DesktopShortcutService.CanonicalInstallExePath();
            var canonicalVer = File.Exists(canonical) ? UpdateService.ReadExeVersion(canonical) : "—";
            var shortcutHint = DesktopShortcutService.ShortcutNeedsRepair(canonical)
                ? "\n\nHinweis: Desktop-Verknüpfung zeigt noch auf eine ältere Kopie — wird beim Start automatisch korrigiert."
                : "";
            KlarWinUpdateStatus.Text = string.IsNullOrWhiteSpace(_pendingKlarWin.Notes)
                ? $"{_pendingKlarWin.Message}\nLäuft: {running} ({runningPath})\nInstalliert: {canonicalVer} ({canonical}){shortcutHint}"
                : $"{_pendingKlarWin.Message}\nLäuft: {running} ({runningPath})\nInstalliert: {canonicalVer} ({canonical})\n{_pendingKlarWin.Notes}{shortcutHint}";
            KlarWinUpdateButton.IsEnabled = _pendingKlarWin.UpdateAvailable;
        }
        catch (Exception ex)
        {
            KlarWinUpdateStatus.Text = UserFriendlyErrors.Translate(ex);
        }
    }

    private async void OnKlarWinCheck(object sender, RoutedEventArgs e) => await RefreshKlarWinUpdateAsync();

    private async void OnKlarWinApply(object sender, RoutedEventArgs e)
    {
        _pendingKlarWin ??= await _update.CheckAsync();
        if (_pendingKlarWin is null || !_pendingKlarWin.UpdateAvailable)
        {
            await RefreshKlarWinUpdateAsync();
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"KlarWin { _pendingKlarWin.LatestVersion} installieren?\n\n" +
            $"Aktuell: {_pendingKlarWin.CurrentVersion}\n" +
            $"Ziel: {DesktopShortcutService.ResolveInstallDirectory()}\n\n" +
            "KlarWin schließt sich kurz und startet neu.",
            "KlarWin — Update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        KlarWinUpdateButton.IsEnabled = false;
        KlarWinUpdateStatus.Text = "Update wird geladen …";
        var progress = new Progress<string>(t => KlarWinUpdateStatus.Text = t);
        var message = await _update.DownloadAndPrepareRestartAsync(_pendingKlarWin, progress);
        if (message == "UPDATE_OK")
        {
            KlarWinUpdateStatus.Text =
                "Update wird installiert — KlarWin startet gleich neu …\n\n" +
                $"Protokoll: {UpdateService.UpdateLogPath}";
            App.ReleaseSingleInstanceForUpdate();
            await Task.Delay(1500);
            Environment.Exit(0);
            return;
        }

        KlarWinUpdateStatus.Text = message;
        KlarWinUpdateButton.IsEnabled = _pendingKlarWin.UpdateAvailable;
    }

    private void OnPickImage(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Hintergrundbild wählen",
            Filter = "Bilder|*.png;*.jpg;*.jpeg;*.bmp;*.webp|Alle Dateien|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            ImagePathBox.Text = dialog.FileName;
            BgImage.IsChecked = true;
        }
    }

    private void OnOpenWebsite(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo { FileName = "https://ganz-soft.de/", UseShellExecute = true });
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        var fresh = new KlarWinSettings
        {
            HideShortcutArrows = _settings.HideShortcutArrows,
            ShowDesktopIcon = _settings.ShowDesktopIcon,
            AutostartBackup = _settings.AutostartBackup,
            NetworkDeviceNotes = _settings.NetworkDeviceNotes,
            LearnedMacVendors = _settings.LearnedMacVendors,
            PromoPosition = _settings.PromoPosition,
            ShowPromoBanner = true
        };
        CopyTheme(fresh, _settings);
        LoadFromSettings();
    }

    private static void CopyTheme(KlarWinSettings from, KlarWinSettings to)
    {
        to.BackgroundMode = from.BackgroundMode;
        to.BackgroundColor1 = from.BackgroundColor1;
        to.BackgroundColor2 = from.BackgroundColor2;
        to.BackgroundColor3 = from.BackgroundColor3;
        to.BackgroundImagePath = from.BackgroundImagePath;
        to.FontFamilyUi = from.FontFamilyUi;
        to.FontFamilyTitle = from.FontFamilyTitle;
        to.FontSizeBody = from.FontSizeBody;
        to.FontSizeTitle = from.FontSizeTitle;
        to.InkColor = from.InkColor;
        to.MuteColor = from.MuteColor;
        to.AccentColor = from.AccentColor;
        to.LeafColor = from.LeafColor;
        to.TileCornerRadius = from.TileCornerRadius;
        to.TileBorderColor = from.TileBorderColor;
        to.TileBorderThickness = from.TileBorderThickness;
        to.TileBackgroundColor = from.TileBackgroundColor;
        to.TileHoverBorderColor = from.TileHoverBorderColor;
        to.ShowPromoBanner = true;
        to.PromoPosition = from.PromoPosition;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnApply(object sender, RoutedEventArgs e)
    {
        SaveToSettings();
        ApplyDesktopShortcut();
        _onApplied();
        Close();
    }
}
