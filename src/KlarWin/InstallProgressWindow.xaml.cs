using System.Windows;
using KlarWin.Services;

namespace KlarWin;

public partial class InstallProgressWindow : Window
{
    public InstallProgressWindow()
    {
        InitializeComponent();
    }

    public void Apply(SoftwareInstallProgress progress)
    {
        if (progress.Total > 0)
        {
            CounterText.Text = progress.IsIndeterminate
                ? $"Schritt {Math.Min(progress.Step + 1, progress.Total)} von {progress.Total}"
                : $"{progress.Step} von {progress.Total} abgeschlossen";
            MainBar.IsIndeterminate = progress.IsIndeterminate;
            if (!progress.IsIndeterminate)
            {
                MainBar.Value = 100.0 * progress.Step / progress.Total;
            }
        }
        else
        {
            CounterText.Text = "";
            MainBar.IsIndeterminate = true;
        }

        HeadlineText.Text = progress.Headline;
        DetailText.Text = progress.Detail;
    }
}
