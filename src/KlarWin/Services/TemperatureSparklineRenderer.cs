using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KlarWin.Services;

public static class TemperatureSparklineRenderer
{
    private const double ScaleMinC = 25;

    public static Color ZoneColor(double celsius, bool isGpu = false)
    {
        var level = TemperatureZones.LevelForCelsius(celsius, isGpu);
        return TemperatureZones.LevelColor(level);
    }

    public static bool IsGpuSensor(string sensorName) =>
        TemperatureZones.IsGpuSensor(sensorName);

    public static void Draw(Canvas canvas, IReadOnlyList<double> values, double? latestCelsius, bool isGpu = false)
    {
        canvas.Children.Clear();
        var width = canvas.ActualWidth > 4 ? canvas.ActualWidth : 280;
        var height = canvas.ActualHeight > 4 ? canvas.ActualHeight : 48;

        var (warn, crit) = TemperatureZones.Thresholds(isGpu);
        var scaleMax = isGpu ? 105 : 100;
        DrawZoneBands(canvas, width, height, warn, crit, scaleMax);

        if (values.Count < 2)
        {
            return;
        }

        var range = Math.Max(scaleMax - ScaleMinC, 1);
        var stroke = latestCelsius is double c ? ZoneColor(c, isGpu) : Color.FromRgb(0x3B, 0x5A, 0x72);

        var points = new PointCollection(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var x = i * width / (values.Count - 1);
            var normalized = Math.Clamp((values[i] - ScaleMinC) / range, 0, 1);
            var y = height - normalized * (height - 4) - 2;
            points.Add(new Point(x, y));
        }

        canvas.Children.Add(new Polyline
        {
            Stroke = new SolidColorBrush(stroke),
            StrokeThickness = 2,
            Points = points
        });
    }

    private static void DrawZoneBands(
        Canvas canvas,
        double width,
        double height,
        double warnC,
        double critC,
        double scaleMax)
    {
        var range = Math.Max(scaleMax - ScaleMinC, 1);
        var warnRatio = Math.Clamp((warnC - ScaleMinC) / range, 0, 1);
        var critRatio = Math.Clamp((critC - ScaleMinC) / range, 0, 1);

        // Oben = hohe Temperatur (rot), unten = niedrig (grün)
        AddBand(canvas, width, height, 1, critRatio, Color.FromArgb(28, 0xB8, 0x3A, 0x3A));
        AddBand(canvas, width, height, critRatio, warnRatio, Color.FromArgb(24, 0xC9, 0x9A, 0x2E));
        AddBand(canvas, width, height, warnRatio, 0, Color.FromArgb(20, 0x2F, 0x6A, 0x4A));
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
