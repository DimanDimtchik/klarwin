using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KlarWin.Services;

public static class FeatureListUi
{
    private static readonly SolidColorBrush LabelBrush = new(Color.FromRgb(0x4A, 0x5C, 0x54));
    private static readonly SolidColorBrush AdminBg = new(Color.FromRgb(0x2F, 0x6A, 0x4A));
    private static readonly SolidColorBrush AdminFg = new(Color.FromRgb(0xF4, 0xF8, 0xF5));

    public static void Fill(StackPanel panel, IReadOnlyList<CatalogFeature> features)
    {
        panel.Children.Clear();
        foreach (var feature in features)
        {
            panel.Children.Add(BuildLine(feature));
        }
    }

    private static UIElement BuildLine(CatalogFeature feature)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 4)
        };

        row.Children.Add(new TextBlock
        {
            Text = feature.Label,
            FontSize = 13,
            Foreground = LabelBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });

        if (feature.RequiresAdmin)
        {
            row.Children.Add(CreateAdminBadge());
        }

        return row;
    }

    private static Border CreateAdminBadge()
    {
        return new Border
        {
            Background = AdminBg,
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(5, 0, 5, 0),
            Height = 17,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "ADMIN",
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 9,
                FontWeight = FontWeights.SemiBold,
                Foreground = AdminFg,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        };
    }
}
