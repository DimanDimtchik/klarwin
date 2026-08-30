using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KlarWin.Services;

namespace KlarWin;

public partial class MainWindow : Window
{
    private readonly PerformanceService _performance = new();
    private readonly CleanupService _cleanup = new();
    private readonly SpeedService _speed = new();
    private readonly ShortcutOverlayService _overlay = new();
    private readonly AutostartService _autostart = new();
    private readonly SecureWipeService _wipe = new();
    private readonly RecoveryService _recovery = new();
    private readonly LicenseKeyService _keys = new();
    private readonly NetworkMonitorService _network = new();
    private readonly AiLoadService _ai = new();
    private readonly UpdateService _update = new();
    private UpdateInfo? _pendingUpdate;
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

    public MainWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => _ = RefreshLiveAsync();
        Loaded += (_, _) =>
        {
            VersionLabel.Text = $"Version {UpdateService.CurrentVersion}";
            _ = RefreshLiveAsync();
            _timer.Start();
            _ = QuietUpdateCheckAsync();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _wipeCts?.Cancel();
            _network.Dispose();
            _performance.Dispose();
        };
    }

    private async Task RefreshLiveAsync()
    {
        var perf = _performance.Capture();
        CpuBar.Width = BarWidth(perf.CpuPercent);
        RamBar.Width = BarWidth(perf.RamPercent);
        DiskBar.Width = BarWidth(100 - perf.DiskFreePercent);
        CpuLabel.Text = $"{perf.CpuPercent:0}%";
        RamLabel.Text = $"{perf.RamPercent:0}%";
        DiskLabel.Text = $"{perf.DiskFreePercent:0}% frei";

        var net = _network.ReadLocal();
        ApplyNetworkBars(net);
        var ai = _ai.Capture(_aiFilter, perf.CpuPercent, perf.RamTotalBytes);
        ApplyAiBars(ai);

        if (_mode == "network")
        {
            ShowNetworkPanel(net, refreshList: false);
        }
        else if (_mode == "ai")
        {
            ShowAiPanel(ai, refreshApps: false);
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

    private static double BarWidth(double percent) => Math.Max(0, Math.Min(180, 180 * percent / 100));

    private void ResetPanelChrome()
    {
        WipeModeBox.Visibility = Visibility.Collapsed;
        FilterBox.Visibility = Visibility.Collapsed;
        DetailList.Visibility = Visibility.Collapsed;
        DetailList.Items.Clear();
        SecondaryAction.Visibility = Visibility.Collapsed;
        PrimaryAction.Visibility = Visibility.Visible;
        PrimaryAction.IsEnabled = true;
    }

    private void OnCleanClick(object sender, RoutedEventArgs e)
    {
        _mode = "clean";
        ResetPanelChrome();
        PanelTitle.Text = "Speicher bereinigen";
        PrimaryAction.Content = "Jetzt bereinigen";
        SecondaryAction.Content = "Vorschau";
        SecondaryAction.Visibility = Visibility.Visible;
        PanelBody.Text = "Vorschau zeigt, wie viel Temp/Cache weg kann. Papierkorb wird mitgeleert.";
        _ = ShowCleanupPreviewAsync();
    }

    private void OnSpeedClick(object sender, RoutedEventArgs e)
    {
        _mode = "speed";
        ResetPanelChrome();
        PanelTitle.Text = "Windows beschleunigen";
        PanelBody.Text = "Stellt Hochleistung ein, reduziert Animationen und leert den DNS-Cache.";
        PrimaryAction.Content = "Tempo anwenden";
    }

    private void OnPerfClick(object sender, RoutedEventArgs e)
    {
        _mode = "perf";
        ResetPanelChrome();
        PrimaryAction.Visibility = Visibility.Collapsed;
        var snap = _performance.Capture();
        PanelTitle.Text = "Leistung";
        PanelBody.Text = $"CPU {snap.CpuPercent:0}% · RAM {CleanupService.FormatBytes(snap.RamUsedBytes)} von {CleanupService.FormatBytes(snap.RamTotalBytes)} · {snap.DiskName} noch {CleanupService.FormatBytes(snap.DiskFreeBytes)} frei";
    }

    private void OnOverlayClick(object sender, RoutedEventArgs e)
    {
        _mode = "overlay";
        ResetPanelChrome();
        PanelTitle.Text = "Verknüpfungspfeile";
        PanelBody.Text = "Blendet den kleinen Pfeil auf Desktop-Verknüpfungen aus. Updates setzen ihn oft wieder.";
        PrimaryAction.Content = "Pfeile ausblenden";
        SecondaryAction.Content = "Pfeile wiederherstellen";
        SecondaryAction.Visibility = Visibility.Visible;
    }

    private void OnAutostartClick(object sender, RoutedEventArgs e)
    {
        _mode = "autostart";
        ResetPanelChrome();
        PanelTitle.Text = "Autostart";
        PrimaryAction.Content = "Auswahl deaktivieren";
        SecondaryAction.Content = "Task-Manager";
        SecondaryAction.Visibility = Visibility.Visible;
        DetailList.Visibility = Visibility.Visible;
        _autostartItems = _autostart.ListEntries();
        DetailList.Items.Clear();
        foreach (var item in _autostartItems)
        {
            DetailList.Items.Add($"{item.Name}  ·  {item.Location}");
        }

        PanelBody.Text = _autostartItems.Count == 0
            ? "Keine klassischen Autostart-Einträge gefunden."
            : $"{_autostartItems.Count} Einträge. Markieren und deaktivieren — oder Task-Manager öffnen.";
    }

    private void OnWipeClick(object sender, RoutedEventArgs e)
    {
        _mode = "wipe";
        ResetPanelChrome();
        WipeModeBox.Visibility = Visibility.Visible;
        PanelTitle.Text = "Freien Speicher überschreiben";
        PrimaryAction.Content = "Überschreiben starten";
        SecondaryAction.Content = "Abbrechen";
        SecondaryAction.Visibility = Visibility.Visible;

        var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var ssd = _wipe.LooksLikeSsd(root);
        var free = new DriveInfo(root).AvailableFreeSpace;
        PanelBody.Text = ssd
            ? $"SSD erkannt auf {root}. Freier Speicher ({CleanupService.FormatBytes(free)}) wird überschrieben — Dateien bleiben. Auf SSDs reicht meist 1× Nullen; Gutmann ist sehr langsam und oft unnötig."
            : $"Freier Speicher auf {root} ({CleanupService.FormatBytes(free)}) wird überschrieben. Vorhandene Dateien bleiben. Gutmann (35×) dauert sehr lange.";
    }

    private void OnRecoverClick(object sender, RoutedEventArgs e)
    {
        _mode = "recover";
        ResetPanelChrome();
        PanelTitle.Text = "Papierkorb wiederherstellen";
        PrimaryAction.Content = "Auswahl wiederherstellen";
        SecondaryAction.Content = "Liste aktualisieren";
        SecondaryAction.Visibility = Visibility.Visible;
        DetailList.Visibility = Visibility.Visible;
        RefreshRecoverList();
    }

    private void OnKeysClick(object sender, RoutedEventArgs e)
    {
        _mode = "keys";
        ResetPanelChrome();
        PanelTitle.Text = "Software-Keys sichern";
        PrimaryAction.Content = "Als Datei speichern";
        SecondaryAction.Content = "Erneut lesen";
        SecondaryAction.Visibility = Visibility.Visible;
        DetailList.Visibility = Visibility.Visible;
        RefreshKeyList();
    }

    private void OnNetworkClick(object sender, RoutedEventArgs e)
    {
        _mode = "network";
        ResetPanelChrome();
        PanelTitle.Text = "Netzwerk";
        PrimaryAction.Content = "Aktualisieren";
        SecondaryAction.Content = "Router öffnen";
        SecondaryAction.Visibility = Visibility.Visible;
        DetailList.Visibility = Visibility.Visible;
        ShowNetworkPanel(_network.Last, refreshList: true);
        _ = ForceNetworkRefreshAsync();
    }

    private void OnAiClick(object sender, RoutedEventArgs e)
    {
        _mode = "ai";
        ResetPanelChrome();
        FilterBox.Visibility = Visibility.Visible;
        PanelTitle.Text = "KI-Last";
        PrimaryAction.Visibility = Visibility.Collapsed;
        var perf = _performance.Capture();
        var ai = _ai.Capture(_aiFilter, perf.CpuPercent, perf.RamTotalBytes);
        ShowAiPanel(ai, refreshApps: true);
    }

    private void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        _mode = "update";
        ResetPanelChrome();
        PanelTitle.Text = "Update";
        PrimaryAction.Content = "Jetzt aktualisieren";
        SecondaryAction.Content = "Erneut prüfen";
        SecondaryAction.Visibility = Visibility.Visible;
        PanelBody.Text = $"Installierte Version: {UpdateService.CurrentVersion}. Prüfe Server …";
        PrimaryAction.Visibility = Visibility.Collapsed;
        _ = CheckUpdateAsync(applyPrompt: false);
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filterUpdating || _mode != "ai") return;
        _aiFilter = FilterBox.SelectedItem is ComboBoxItem item && item.Tag is string tag ? tag : "all";
        var perf = _performance.Capture();
        ShowAiPanel(_ai.Capture(_aiFilter, perf.CpuPercent, perf.RamTotalBytes), refreshApps: false);
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
            case "wipe":
                await RunWipe();
                break;
            case "recover":
                RestoreSelected();
                break;
            case "keys":
                ExportKeys();
                break;
            case "network":
                await ForceNetworkRefreshAsync();
                break;
            case "update":
                await ApplyUpdateAsync();
                break;
        }
    }

    private void OnSecondaryAction(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case "clean":
                _ = ShowCleanupPreviewAsync();
                break;
            case "overlay":
                ApplyOverlay(hide: false);
                break;
            case "autostart":
                _autostart.OpenTaskManagerStartup();
                break;
            case "wipe":
                _wipeCts?.Cancel();
                PanelBody.Text = "Abbruch angefordert …";
                break;
            case "recover":
                RefreshRecoverList();
                break;
            case "keys":
                RefreshKeyList();
                break;
            case "network":
                _network.OpenRouterPage();
                break;
            case "update":
                _ = CheckUpdateAsync(applyPrompt: false);
                break;
        }
    }

    private async Task ShowCleanupPreviewAsync()
    {
        PanelBody.Text = "Vorschau wird berechnet …";
        var preview = await Task.Run(() => _cleanup.Preview());
        if (preview.Items.Count == 0)
        {
            PanelBody.Text = "Kaum etwas zum Aufräumen gefunden.";
            return;
        }

        var lines = string.Join(" · ", preview.Items.Select(i => $"{i.Label}: {CleanupService.FormatBytes(i.Bytes)}"));
        PanelBody.Text = $"Vorschau: {lines}. Gesamt ca. {CleanupService.FormatBytes(preview.TotalBytes)} ({preview.TotalFiles} Dateien). Papierkorb kommt beim Bereinigen dazu.";
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

    private void DisableSelectedAutostart()
    {
        if (DetailList.SelectedIndex < 0 || DetailList.SelectedIndex >= _autostartItems.Count)
        {
            PanelBody.Text = "Bitte einen Eintrag in der Liste markieren.";
            return;
        }

        var entry = _autostartItems[DetailList.SelectedIndex];
        var confirm = MessageBox.Show(
            this,
            $"„{entry.Name}“ wirklich aus dem Autostart nehmen?",
            "KlarWin",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            PanelBody.Text = _autostart.Disable(entry);
            OnAutostartClick(this, new RoutedEventArgs());
        }
        catch (Exception ex)
        {
            PanelBody.Text = $"Fehler: {ex.Message}. Bei System-Einträgen als Administrator starten.";
        }
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
            "Vorhandene Dateien und Programme bleiben.\n" +
            "Nur bereits gelöschte Reste im freien Speicher werden unbrauchbar.\n\n" +
            "Das kann lange dauern. Fortfahren?",
            "KlarWin — Überschreiben",
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
        PanelBody.Text = result.Message;
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

    private void RefreshRecoverList()
    {
        _recoverItems = _recovery.ListRecycleBin();
        DetailList.Items.Clear();
        foreach (var item in _recoverItems)
        {
            var size = item.SizeBytes > 0 ? CleanupService.FormatBytes(item.SizeBytes) : "Ordner";
            DetailList.Items.Add($"{item.DisplayName}  ·  {size}  ·  {item.DeletedAt:dd.MM.yyyy HH:mm}");
        }

        PanelBody.Text = _recoverItems.Count == 0
            ? "Papierkorb ist leer oder nicht lesbar. Endgültig gelöschte Dateien (Shift+Entf) braucht spezialisierte Forensik — das ist hier nicht drin."
            : $"{_recoverItems.Count} Einträge im Papierkorb. Markieren und wiederherstellen.";
    }

    private void RestoreSelected()
    {
        if (DetailList.SelectedIndex < 0 || DetailList.SelectedIndex >= _recoverItems.Count)
        {
            PanelBody.Text = "Bitte eine Datei in der Liste markieren.";
            return;
        }

        var item = _recoverItems[DetailList.SelectedIndex];
        PanelBody.Text = _recovery.Restore(item);
        RefreshRecoverList();
    }

    private void RefreshKeyList()
    {
        _keyItems = _keys.Collect();
        DetailList.Items.Clear();
        foreach (var item in _keyItems)
        {
            var masked = MaskKey(item.Key);
            DetailList.Items.Add($"{item.Product}  ·  {masked}");
        }

        PanelBody.Text = _keyItems.Count == 0
            ? "Keine Keys gefunden. Moderne Windows/Office-Lizenzen sind oft digital (Microsoft-Konto) und stehen nicht als klassischer Product Key in der Registry."
            : $"{_keyItems.Count} Keys gefunden. „Als Datei speichern“ legt eine Sicherung unter Dokumente\\KlarWin ab.";
    }

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

    private static string MaskKey(string key)
    {
        if (key.Length <= 8) return key;
        var visible = Math.Min(5, key.Length / 4);
        return key[..visible] + new string('•', Math.Min(12, key.Length - visible - 4)) + key[^4..];
    }

    private async Task ForceNetworkRefreshAsync()
    {
        PanelBody.Text = "Router wird abgefragt …";
        await _network.PingGatewayAsync();
        var snap = await _network.RefreshRouterAsync(includeHosts: true);
        _lastRouterUtc = DateTime.UtcNow;
        _lastHostsUtc = DateTime.UtcNow;
        ApplyNetworkBars(snap);
        ShowNetworkPanel(snap, refreshList: true);
    }

    private void ShowNetworkPanel(NetworkSnapshot net, bool refreshList)
    {
        var down = net.RouterReachable ? net.WanDownBytesPerSec : net.AdapterDownBytesPerSec;
        var up = net.RouterReachable ? net.WanUpBytesPerSec : net.AdapterUpBytesPerSec;
        var parts = new List<string>
        {
            $"{net.AdapterName}: ↓ {NetworkMonitorService.FormatRate(net.AdapterDownBytesPerSec)} ↑ {NetworkMonitorService.FormatRate(net.AdapterUpBytesPerSec)}"
        };

        if (net.RouterReachable)
        {
            var uptime = net.WanUptime.TotalHours >= 1
                ? $"{net.WanUptime.TotalHours:0} h"
                : $"{net.WanUptime.Minutes} min";
            parts.Add($"{net.RouterName} · {net.WanType} {net.WanStatus} · Leitung ↓ {NetworkMonitorService.FormatBits(net.WanDownBitsMax)} ↑ {NetworkMonitorService.FormatBits(net.WanUpBitsMax)}");
            parts.Add($"WAN jetzt ↓ {NetworkMonitorService.FormatRate(down)} ↑ {NetworkMonitorService.FormatRate(up)} · Volumen seit Verbindung {CleanupService.FormatBytes(net.WanBytesReceived)} ↓ / {CleanupService.FormatBytes(net.WanBytesSent)} ↑ · online {uptime}");
            if (!string.IsNullOrWhiteSpace(net.ExternalIp))
            {
                parts.Add($"öffentliche IP {net.ExternalIp}");
            }

            parts.Add($"{net.ActiveHostCount} Geräte aktiv, {net.HostCount} bekannt");
        }

        if (!string.IsNullOrWhiteSpace(net.Note))
        {
            parts.Add(net.Note);
        }

        PanelBody.Text = string.Join("  ·  ", parts);

        if (!refreshList) return;
        DetailList.Items.Clear();
        foreach (var host in net.Hosts.Where(h => h.Active).Take(40))
        {
            DetailList.Items.Add($"{host.Name}  ·  {host.Ip}  ·  {host.InterfaceType}");
        }

        foreach (var host in net.Hosts.Where(h => !h.Active).Take(8))
        {
            DetailList.Items.Add($"{host.Name} (offline)  ·  {host.Ip}");
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
            ? $"{ai.GpuName}: {ai.GpuPercent:0}% · VRAM {CleanupService.FormatBytes(ai.GpuMemoryUsedBytes)} von {CleanupService.FormatBytes(ai.GpuMemoryTotalBytes)}"
            : "Keine NVIDIA-GPU erkannt";
        var onGpu = ai.SelectedOnGpu ? "läuft auf der GPU" : "aktuell nicht auf der GPU";
        PanelBody.Text = $"{ai.FilterLabel}: {ai.CpuPercent:0}% der CPU (System {ai.SystemCpuPercent:0}%) · {CleanupService.FormatBytes(ai.RamBytes)} RAM ({ai.RamPercent:0}%) · {onGpu}. {gpu}";

        var selectedIndex = DetailList.SelectedIndex;
        DetailList.Visibility = Visibility.Visible;
        DetailList.Items.Clear();
        if (ai.Rows.Count == 0)
        {
            DetailList.Items.Add("Keine passenden KI-Prozesse gefunden (Cursor, Ollama, Llama, LM Studio, ChatGPT …).");
            return;
        }

        foreach (var row in ai.Rows)
        {
            var gpuMark = row.OnGpu ? " · GPU" : "";
            var count = row.Count > 1 ? $" ({row.Count})" : "";
            DetailList.Items.Add($"{row.Name}{count}  ·  {row.CpuPercent:0.0}% CPU  ·  {CleanupService.FormatBytes(row.RamBytes)}{gpuMark}");
        }

        if (selectedIndex >= 0 && selectedIndex < DetailList.Items.Count)
        {
            DetailList.SelectedIndex = selectedIndex;
        }
    }

    private async Task QuietUpdateCheckAsync()
    {
        var info = await _update.CheckAsync();
        if (!info.UpdateAvailable) return;
        VersionLabel.Text = $"Version {info.CurrentVersion} · Update {info.LatestVersion} verfügbar";
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
        PanelBody.Text = message;
        if (message.Contains("startet", StringComparison.OrdinalIgnoreCase))
        {
            Application.Current.Shutdown();
        }
        else
        {
            PrimaryAction.IsEnabled = true;
        }
    }
}
