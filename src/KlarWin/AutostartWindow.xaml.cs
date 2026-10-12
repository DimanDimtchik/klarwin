using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using KlarWin.Services;

namespace KlarWin;

public sealed class AutostartRow : INotifyPropertyChanged
{
    private bool _isChecked;
    private bool _suppressToggle;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
            if (!_suppressToggle)
                CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string Title { get; init; } = "";
    public string Subtitle { get; set; } = "";
    public AutostartEntry? Entry { get; init; }
    public AutostartCandidate? Candidate { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? CheckedChanged;

    public void SetCheckedSilent(bool value)
    {
        _suppressToggle = true;
        IsChecked = value;
        _suppressToggle = false;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class AutostartWindow : Window
{
    private readonly AutostartService _autostart = new();
    private readonly AutostartCatalogService _catalog = new();
    private readonly ObservableCollection<AutostartRow> _entryRows = [];
    private readonly ObservableCollection<AutostartRow> _candidateRows = [];
    private bool _pickerMode;

    /// <summary>True when the user asked to open Startlayout after closing this window.</summary>
    public bool OpenStartLayoutRequested { get; private set; }

    public AutostartWindow()
    {
        InitializeComponent();
        EntryList.ItemsSource = _entryRows;
        CandidateList.ItemsSource = _candidateRows;
        Loaded += (_, _) => LoadEntries();
    }

    private void LoadEntries()
    {
        _pickerMode = false;
        ShowEntryChrome();
        _entryRows.Clear();
        foreach (var entry in _autostart.ListEntries())
        {
            var row = new AutostartRow
            {
                Title = entry.Name,
                Subtitle = FormatEntrySubtitle(entry),
                Entry = entry
            };
            row.SetCheckedSilent(entry.Enabled);
            row.CheckedChanged += OnEntryCheckedChanged;
            _entryRows.Add(row);
        }

        var active = _entryRows.Count(r => r.Entry?.Enabled == true);
        HintText.Text = _entryRows.Count == 0
            ? "Keine Autostart-Einträge. „Programm hinzufügen“ zeigt installierte Apps — inkl. Store-Apps."
            : $"{_entryRows.Count} Einträge, {active} aktiv. Haken = startet mit Windows (wie Task-Manager).";
    }

    private static string FormatEntrySubtitle(AutostartEntry entry)
    {
        var status = entry.Enabled ? "Aktiv" : "Inaktiv (deaktiviert)";
        return $"{FriendlyCommand(entry)} · {entry.Location} · {status}";
    }

    private void OnEntryCheckedChanged(object? sender, EventArgs e)
    {
        if (sender is not AutostartRow { Entry: { } entry } row)
            return;

        try
        {
            var msg = _autostart.SetEnabled(entry, row.IsChecked);
            // Reload so Enabled/StartupApproved status stays accurate
            LoadEntries();
            HintText.Text = msg + "\n" + HintText.Text;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, UserFriendlyErrors.Translate(ex), "KlarWin",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            LoadEntries();
        }
    }

    private static string FriendlyCommand(AutostartEntry entry)
    {
        if (entry.Location.Contains("Ordner", StringComparison.Ordinal))
        {
            var target = AutostartCatalogService.ResolveShortcutTarget(entry.Command) ?? entry.Command;
            return Path.GetFileName(target);
        }

        var exe = AutostartService.ExtractExecutablePath(entry.Command);
        if (!string.IsNullOrWhiteSpace(exe)
            && !Path.GetFileName(exe).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            return Path.GetFileName(exe);

        // Protocol / AppX (z. B. BlueMail über explorer.exe + App-ID)
        var cmd = entry.Command.Trim();
        if (cmd.Length > 64) cmd = cmd[..61] + "…";
        return cmd;
    }

    private void ShowEntryChrome()
    {
        EntryList.Visibility = Visibility.Visible;
        CandidateList.Visibility = Visibility.Collapsed;
        BtnDisable.Visibility = Visibility.Collapsed;
        BtnEnable.Visibility = Visibility.Collapsed;
        BtnAdd.Visibility = Visibility.Visible;
        BtnStartupFolder.Visibility = Visibility.Visible;
        BtnStartLayout.Visibility = Visibility.Visible;
        BtnBack.Visibility = Visibility.Collapsed;
        BtnConfirmAdd.Visibility = Visibility.Collapsed;
        HeaderText.Text = "Autostart";
    }

    private void ShowPickerChrome()
    {
        EntryList.Visibility = Visibility.Collapsed;
        CandidateList.Visibility = Visibility.Visible;
        BtnDisable.Visibility = Visibility.Collapsed;
        BtnEnable.Visibility = Visibility.Collapsed;
        BtnAdd.Visibility = Visibility.Collapsed;
        BtnStartupFolder.Visibility = Visibility.Collapsed;
        BtnStartLayout.Visibility = Visibility.Collapsed;
        BtnBack.Visibility = Visibility.Visible;
        BtnConfirmAdd.Visibility = Visibility.Visible;
        HeaderText.Text = "Programm zum Autostart";
    }

    private void OnStartLayoutClick(object sender, RoutedEventArgs e)
    {
        OpenStartLayoutRequested = true;
        Close();
    }

    private async void OnAddProgramsClick(object sender, RoutedEventArgs e)
    {
        _pickerMode = true;
        ShowPickerChrome();
        _candidateRows.Clear();
        HintText.Text = "Installierte Programme werden gesammelt (Startmenü, Store, Registry) …";
        BtnConfirmAdd.IsEnabled = false;

        var current = _autostart.ListEntries();
        var candidates = await Task.Run(() => _catalog.DiscoverCandidates(current));
        _candidateRows.Clear();
        foreach (var c in candidates)
        {
            _candidateRows.Add(new AutostartRow
            {
                Title = c.Name,
                Subtitle = $"{Path.GetFileName(c.ExecutablePath)} · {c.Source}",
                Candidate = c
            });
        }

        BtnConfirmAdd.IsEnabled = true;
        HintText.Text = _candidateRows.Count == 0
            ? "Keine weiteren Programme gefunden (bereits im Autostart sind ausgeblendet)."
            : $"{_candidateRows.Count} Programme — anhaken und „Zum Autostart hinzufügen“. " +
              "Schon aktive (z. B. Nextcloud, BlueMail) erscheinen hier nicht.";
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_pickerMode)
            LoadEntries();
    }

    private void OnConfirmAddClick(object sender, RoutedEventArgs e)
    {
        var selected = _candidateRows.Where(r => r.IsChecked && r.Candidate is not null).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Bitte mindestens ein Programm auswählen.", "KlarWin",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var messages = selected.Select(row => _autostart.AddToAutostart(row.Candidate!)).ToList();
        MessageBox.Show(this, string.Join("\n", messages), "KlarWin",
            MessageBoxButton.OK, MessageBoxImage.Information);
        LoadEntries();
    }

    private void OnDisableClick(object sender, RoutedEventArgs e) { /* replaced by checkbox toggle */ }

    private void OnEnableClick(object sender, RoutedEventArgs e) { /* replaced by checkbox toggle */ }

    private void OnStartupFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = folder,
            UseShellExecute = true
        });
    }
}
