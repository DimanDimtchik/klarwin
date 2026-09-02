using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KlarWin.Services;

public static class DiskUsageBarRenderer
{
    private static readonly Color UsedColor = Color.FromRgb(0xB8, 0x3A, 0x3A);
    private static readonly Color FreeColor = Color.FromRgb(0xD5, 0xE0, 0xD8);
    private static readonly Color BorderColor = Color.FromRgb(0xC3, 0xD1, 0xC8);

    public static void Draw(Canvas canvas, long usedBytes, long totalBytes)
    {
        canvas.Children.Clear();
        if (totalBytes <= 0)
        {
            return;
        }

        var width = canvas.ActualWidth > 4 ? canvas.ActualWidth : 420;
        var height = canvas.ActualHeight > 4 ? canvas.ActualHeight : 32;
        var usedRatio = Math.Clamp((double)usedBytes / totalBytes, 0, 1);
        var usedWidth = Math.Max(0, width * usedRatio);
        var freeWidth = Math.Max(0, width - usedWidth);

        canvas.Children.Add(new Rectangle
        {
            Width = width,
            Height = height,
            RadiusX = 4,
            RadiusY = 4,
            Stroke = new SolidColorBrush(BorderColor),
            StrokeThickness = 1,
            Fill = new SolidColorBrush(FreeColor)
        });

        if (usedWidth > 0.5)
        {
            var usedRect = new Rectangle
            {
                Width = usedWidth,
                Height = height - 2,
                RadiusX = 3,
                RadiusY = 3,
                Fill = new SolidColorBrush(UsedColor)
            };
            Canvas.SetLeft(usedRect, 1);
            Canvas.SetTop(usedRect, 1);
            canvas.Children.Add(usedRect);
        }

        if (usedRatio is > 0.08 and < 0.92)
        {
            var divider = new Line
            {
                X1 = usedWidth,
                X2 = usedWidth,
                Y1 = 2,
                Y2 = height - 2,
                Stroke = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)),
                StrokeThickness = 1
            };
            canvas.Children.Add(divider);
        }
    }

    public static string FormatLegend(string diskName, long usedBytes, long freeBytes, long totalBytes)
    {
        var letter = diskName.TrimEnd(':').Trim();
        var usedPct = totalBytes > 0 ? usedBytes * 100.0 / totalBytes : 0;
        return
            $"{letter}: Belegt {ByteSizeFormatter.Format(usedBytes, ByteSizeKind.Disk)} ({usedPct:0} %) · " +
            $"Frei {ByteSizeFormatter.Format(freeBytes, ByteSizeKind.Disk)}";
    }
}
