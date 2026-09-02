using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KlarWin.Services;

public static class HealthBarRenderer
{
    public static Border CreateCategoryRow(HealthCategory category)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });

        grid.Children.Add(new TextBlock
        {
            Text = category.Label,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x18, 0x24, 0x1F)),
            VerticalAlignment = VerticalAlignment.Center
        });

        var track = new Grid
        {
            Height = 10,
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var fill = Math.Clamp(category.BarPercent, 0, 100);
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(fill, 0.01), GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(100 - fill, 0.01), GridUnitType.Star) });
        track.Children.Add(new Border
        {
            Background = new SolidColorBrush(TemperatureZones.LevelColor(category.Level)),
            CornerRadius = new CornerRadius(2)
        });
        var trackBg = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(40, 0x18, 0x24, 0x1F)),
            CornerRadius = new CornerRadius(2)
        };
        Grid.SetColumn(trackBg, 1);
        track.Children.Add(trackBg);
        Grid.SetColumn(track, 1);
        grid.Children.Add(track);

        var status = new TextBlock
        {
            Text = TemperatureZones.LevelLabel(category.Level),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = new SolidColorBrush(TemperatureZones.LevelColor(category.Level))
        };
        Grid.SetColumn(status, 2);
        grid.Children.Add(status);

        return new Border { Child = grid };
    }

    public static void DrawZoneLegend(Canvas canvas, double width, double height, bool isGpu = false)
    {
        canvas.Children.Clear();
        var warnY = isGpu ? 0.35 : 0.40;
        var critY = isGpu ? 0.15 : 0.20;
        AddBand(canvas, width, height, 1, critY, Color.FromArgb(28, 0xB8, 0x3A, 0x3A));
        AddBand(canvas, width, height, critY, warnY, Color.FromArgb(24, 0xC9, 0x9A, 0x2E));
        AddBand(canvas, width, height, warnY, 0, Color.FromArgb(20, 0x2F, 0x6A, 0x4A));
    }

    private static void AddBand(Canvas canvas, double width, double height, double topRatio, double bottomRatio, Color color)
    {
        var top = height * (1 - topRatio);
        var bottom = height * (1 - bottomRatio);
        canvas.Children.Add(new Rectangle
        {
            Width = width,
            Height = Math.Max(0, bottom - top),
            Fill = new SolidColorBrush(color)
        });
        Canvas.SetTop(canvas.Children[^1], top);
    }
}
