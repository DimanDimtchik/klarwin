using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KlarWin.Services;

namespace KlarWin;

public partial class UpdateExternalLinksDialog : Window
{
    public UpdateExternalLinksDialog(string message, IReadOnlyList<SoftwareUpdateExternalAction> actions)
    {
        InitializeComponent();
        MessageText.Text = message;
        BuildRows(actions);
    }

    public static void ShowForOwner(Window owner, string message, IReadOnlyList<SoftwareUpdateExternalAction> actions)
    {
        if (actions.Count == 0) return;
        var dlg = new UpdateExternalLinksDialog(message, actions) { Owner = owner };
        dlg.ShowDialog();
    }

    private void BuildRows(IReadOnlyList<SoftwareUpdateExternalAction> actions)
    {
        ActionsPanel.Children.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var action in actions)
        {
            var key = action.Kind + "|" + action.PackageId + "|" + action.Uri;
            if (!seen.Add(key)) continue;

            var border = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE7, 0xE0, 0xD8)),
                BorderThickness = new Thickness(1),
                Background = Brushes.White,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel();
            text.Children.Add(new TextBlock
            {
                Text = action.PackageName,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap
            });
            text.Children.Add(new TextBlock
            {
                Text = action.Hint,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0x5C, 0x54)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            var btn = new Button
            {
                Content = action.ButtonLabel,
                Style = (Style)FindResource("GhostButton"),
                Margin = new Thickness(12, 0, 0, 0),
                Padding = new Thickness(12, 8, 12, 8),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = action
            };
            btn.Click += OnActionClick;
            Grid.SetColumn(btn, 1);
            grid.Children.Add(btn);

            border.Child = grid;
            ActionsPanel.Children.Add(border);
        }
    }

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SoftwareUpdateExternalAction action })
            return;

        if (!action.TryOpen())
        {
            MessageBox.Show(
                this,
                $"Konnte „{action.ButtonLabel}“ nicht öffnen.\n\nZiel: {action.Uri}",
                "KlarWin",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
