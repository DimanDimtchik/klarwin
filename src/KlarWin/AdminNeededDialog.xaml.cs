using System.Windows;

namespace KlarWin;

public partial class AdminNeededDialog : Window
{
    public bool ElevateRequested { get; private set; }

    public AdminNeededDialog(string message, string detail)
    {
        InitializeComponent();
        MessageText.Text = message;
        DetailText.Text = string.IsNullOrWhiteSpace(detail) ? "—" : detail;
    }

    /// <summary>Shows the dialog. Returns true when the user chose to continue as admin.</summary>
    public static bool ShowForOwner(Window owner, string message, string detail)
    {
        var dlg = new AdminNeededDialog(message, detail) { Owner = owner };
        dlg.ShowDialog();
        return dlg.ElevateRequested;
    }

    private void OnElevateClick(object sender, RoutedEventArgs e)
    {
        ElevateRequested = true;
        DialogResult = true;
        Close();
    }

    private void OnLaterClick(object sender, RoutedEventArgs e)
    {
        ElevateRequested = false;
        DialogResult = false;
        Close();
    }
}
