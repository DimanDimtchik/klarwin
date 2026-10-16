using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;
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
    private readonly ICollectionView _entryView;
    private readonly ICollectionView _candidateView;
    private bool _pickerMode;
    private string _statusHint = "";

    /// <summary>True when the user asked to open Startlayout after closing this window.</summary>
    public bool OpenStartLayoutRequested { get; private set; }

    public AutostartWindow()
    {
        InitializeComponent();
        EntryList.ItemsSource = _entryRows;
        CandidateList.ItemsSource = _candidateRows;
        _entryView = CollectionViewSource.GetDefaultView(_entryRows);
        _candidateView = CollectionViewSource.GetDefaultView(_candidateRows);
        _entryView.Filter = FilterRow;
        _candidateView.Filter = FilterRow;
        Loaded += (_, _) =>
        {
            LoadEntries();
            SearchBox.Focus();
        };
    }

    private bool FilterRow(object obj)
    {
        if (obj is not AutostartRow row) return false;
        var q = SearchBox.Text.Trim();
        if (q.Length == 0) return true;
        return row.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
               || row.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void OnSearchTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    private void ApplyFilter()
    {
        _entryView.Refresh();
        _candidateView.Refresh();
        UpdateFilterHint();
    }

    private void UpdateFilterHint()
    {
        var q = SearchBox.Text.Trim();
        if (q.Length == 0)
        {
            HintText.Text = _statusHint;
            return;
        }

        var view = _pickerMode ? _candidateView : _entryView;
        var visible = 0;
        foreach (var _ in view)
            visible++;

        var total = _pickerMode ? _candidateRows.Count : _entryRows.Count;
        HintText.Text = $"{visible} von {total} Treffer für „{q}“.\n{_statusHint}";
    }

    private void LoadEntries()
    {
        _pickerMode = false;
        ShowEntryChrome();
        var keepSearch = SearchBox.Text;
        _entryRows.Clear();
        foreach (var entry in _autostart.ListEntries())
        {
            var profileTitle = BrowserProfileCatalogService.TryFormatAutostartLabel(entry.Command);
            var row = new AutostartRow
            {
                Title = string.IsNullOrWhiteSpace(profileTitle) ? entry.Name : profileTitle,
                Subtitle = FormatEntrySubtitle(entry),
                Entry = entry
            };
            row.SetCheckedSilent(entry.Enabled);
            row.CheckedChanged += OnEntryCheckedChanged;
            _entryRows.Add(row);
        }

        var active = _entryRows.Count(r => r.Entry?.Enabled == true);
        _statusHint = _entryRows.Count == 0
            ? "Keine Autostart-Einträge. „Programm hinzufügen“ zeigt installierte Apps — inkl. Store und LocalAppData\\Programs."
            : $"{_entryRows.Count} Einträge, {active} aktiv. Haken = startet mit Windows. Suche oben filtert die Liste.";
        SearchBox.Text = keepSearch;
        ApplyFilter();
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
            LoadEntries();
            _statusHint = msg + "\n" + _statusHint;
            UpdateFilterHint();
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
        var profileLabel = BrowserProfileCatalogService.TryFormatAutostartLabel(entry.Command);
        if (!string.IsNullOrWhiteSpace(profileLabel))
            return profileLabel;

        if (entry.Location.Contains("Ordner", StringComparison.Ordinal))
        {
            var target = AutostartCatalogService.ResolveShortcutTarget(entry.Command) ?? entry.Command;
            return Path.GetFileName(target);
        }

        var exe = AutostartService.ExtractExecutablePath(entry.Command);
        if (!string.IsNullOrWhiteSpace(exe)
            && !Path.GetFileName(exe).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            return Path.GetFileName(exe);

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
        BtnBrowseExe.Visibility = Visibility.Collapsed;
        BtnConfirmAdd.Visibility = Visibility.Collapsed;
        HeaderText.Text = "Autostart";
        SearchBox.ToolTip = "Autostart-Einträge filtern (Name oder Pfad)";
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
        BtnBrowseExe.Visibility = Visibility.Visible;
        BtnConfirmAdd.Visibility = Visibility.Visible;
        HeaderText.Text = "Programm zum Autostart";
        SearchBox.ToolTip = "Installierte Programme filtern";
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
        _statusHint = "Installierte Programme werden gesammelt (Startmenü, Desktop, Angeheftet, LocalAppData\\Programs, Store, Registry) …";
        UpdateFilterHint();
        BtnConfirmAdd.IsEnabled = false;
        BtnBrowseExe.IsEnabled = false;

        var current = _autostart.ListEntries();
        var candidates = await Task.Run(() => _catalog.DiscoverCandidates(current));
        _candidateRows.Clear();
        foreach (var c in candidates)
            _candidateRows.Add(MakeCandidateRow(c));

        BtnConfirmAdd.IsEnabled = true;
        BtnBrowseExe.IsEnabled = true;
        _statusHint = _candidateRows.Count == 0
            ? "Keine weiteren Programme gefunden. „Andere App wählen …“ öffnet jede .exe (auch portable)."
            : $"{_candidateRows.Count} Programme — inkl. Microsoft Store (z. B. BlueMail). Fehlt etwas: „Andere App wählen …“.";
        ApplyFilter();
        SearchBox.Focus();
    }

    private static AutostartRow MakeCandidateRow(AutostartCandidate c)
    {
        var detail = string.IsNullOrWhiteSpace(c.Arguments)
            ? Path.GetFileName(c.ExecutablePath)
            : $"{Path.GetFileName(c.ExecutablePath)} {c.Arguments}";
        return new AutostartRow
        {
            Title = c.Name,
            Subtitle = $"{detail} · {c.Source}",
            Candidate = c
        };
    }

    private void OnBrowseExeClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Programm für Autostart wählen",
            Filter = "Programme (*.exe)|*.exe|Alle Dateien (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        var localPrograms = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs");
        if (Directory.Exists(localPrograms))
            dialog.InitialDirectory = localPrograms;
        else
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (Directory.Exists(pf))
                dialog.InitialDirectory = pf;
        }

        if (dialog.ShowDialog(this) != true)
            return;

        var path = dialog.FileName;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try { path = Path.GetFullPath(path); }
        catch { /* keep as-is */ }

        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Bitte eine .exe-Datei wählen.", "KlarWin",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var name = AppFriendlyName.DescribeExecutable(path);
        if (string.IsNullOrWhiteSpace(name) || name.Length < 2)
            name = Path.GetFileNameWithoutExtension(path);

        var candidate = new AutostartCandidate
        {
            Name = name,
            ExecutablePath = path,
            Arguments = "",
            Source = "Manuell gewählt"
        };

        var existing = _candidateRows.FirstOrDefault(r =>
            r.Candidate is not null
            && string.Equals(r.Candidate.IdentityKey, candidate.IdentityKey, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.SetCheckedSilent(true);
            CandidateList.ScrollIntoView(existing);
            _statusHint = $"„{existing.Title}“ war schon in der Liste — angehakt.";
            UpdateFilterHint();
            return;
        }

        var row = MakeCandidateRow(candidate);
        row.SetCheckedSilent(true);
        _candidateRows.Insert(0, row);
        CandidateList.ScrollIntoView(row);
        _statusHint = $"„{name}“ hinzugefügt und angehakt. Mit „Zum Autostart hinzufügen“ bestätigen.";
        ApplyFilter();
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

    private void OnDisableClick(object sender, RoutedEventArgs e) { }

    private void OnEnableClick(object sender, RoutedEventArgs e) { }

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
