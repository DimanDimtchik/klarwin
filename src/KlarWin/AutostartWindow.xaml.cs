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

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
        }
    }

    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public AutostartEntry? Entry { get; init; }
    public AutostartCandidate? Candidate { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;

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
            var status = entry.Enabled ? "Aktiv" : "Inaktiv";
            _entryRows.Add(new AutostartRow
            {
                Title = entry.Name,
                Subtitle = $"{FriendlyCommand(entry)} ({entry.Location} · {status})",
                Entry = entry
            });
        }

        HintText.Text = _entryRows.Count == 0
            ? "Keine Autostart-Einträge. „Programm hinzufügen“ zeigt installierte Apps — ohne Ordner-Dialog."
            : $"{_entryRows.Count} Einträge — per Checkbox auswählen, dann deaktivieren oder aktivieren.";
    }

    private static string FriendlyCommand(AutostartEntry entry)
    {
        if (entry.Location.Contains("Ordner", StringComparison.Ordinal))
        {
            var target = AutostartCatalogService.ResolveShortcutTarget(entry.Command) ?? entry.Command;
            return Path.GetFileName(target);
        }

        var exe = AutostartService.ExtractExecutablePath(entry.Command);
        return string.IsNullOrWhiteSpace(exe) ? entry.Command : Path.GetFileName(exe);
    }

    private void ShowEntryChrome()
    {
        EntryList.Visibility = Visibility.Visible;
        CandidateList.Visibility = Visibility.Collapsed;
        BtnDisable.Visibility = Visibility.Visible;
        BtnEnable.Visibility = Visibility.Visible;
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
        HintText.Text = "Installierte Programme werden gesammelt …";
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
            ? "Keine weiteren Programme gefunden."
            : $"{_candidateRows.Count} Programme — auswählen und „Zum Autostart hinzufügen“.";
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_pickerMode)
        {
            LoadEntries();
        }
    }

    private void OnConfirmAddClick(object sender, RoutedEventArgs e)
    {
        var selected = _candidateRows.Where(r => r.IsChecked && r.Candidate is not null).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Bitte mindestens ein Programm auswählen.", "KlarWin", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var messages = new List<string>();
        foreach (var row in selected)
        {
            messages.Add(_autostart.AddToAutostart(row.Candidate!));
        }

        MessageBox.Show(this, string.Join("\n", messages), "KlarWin", MessageBoxButton.OK, MessageBoxImage.Information);
        LoadEntries();
    }

    private void OnDisableClick(object sender, RoutedEventArgs e)
    {
        var selected = _entryRows.Where(r => r.IsChecked && r.Entry is { Enabled: true }).Select(r => r.Entry!).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Bitte aktive Einträge auswählen.", "KlarWin", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this, $"{selected.Count} Eintrag/Einträge deaktivieren?", "KlarWin", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var notes = selected.Select(entry => _autostart.Disable(entry)).ToList();
        MessageBox.Show(this, string.Join("\n", notes), "KlarWin", MessageBoxButton.OK, MessageBoxImage.Information);
        LoadEntries();
    }

    private void OnEnableClick(object sender, RoutedEventArgs e)
    {
        var selected = _entryRows.Where(r => r.IsChecked && r.Entry is { Enabled: false }).Select(r => r.Entry!).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Bitte inaktive Einträge auswählen.", "KlarWin", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var notes = selected.Select(entry => _autostart.Enable(entry)).ToList();
        MessageBox.Show(this, string.Join("\n", notes), "KlarWin", MessageBoxButton.OK, MessageBoxImage.Information);
        LoadEntries();
    }

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
