using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KlarWin.Services;

public static class SparklineRenderer
{
    public static void Draw(Canvas canvas, IReadOnlyList<double> values, double maxValue, Color strokeColor, double? minValue = null)
    {
        canvas.Children.Clear();
        if (values.Count < 2 || maxValue <= 0)
        {
            return;
        }

        var width = canvas.ActualWidth > 4 ? canvas.ActualWidth : 180;
        var height = canvas.ActualHeight > 4 ? canvas.ActualHeight : 28;
        var min = minValue ?? 0;
        var range = Math.Max(maxValue - min, 0.001);
        DrawCore(canvas, values, width, height, min, range, strokeColor);
    }

    /// <summary>Scales to min/max of the series so small changes (e.g. RAM %) remain visible.</summary>
    public static void DrawAutoScale(Canvas canvas, IReadOnlyList<double> values, Color strokeColor, double floor = 0, double ceiling = 100)
    {
        canvas.Children.Clear();
        if (values.Count < 2)
        {
            return;
        }

        var width = canvas.ActualWidth > 4 ? canvas.ActualWidth : 180;
        var height = canvas.ActualHeight > 4 ? canvas.ActualHeight : 28;
        var dataMin = values.Min();
        var dataMax = values.Max();
        var min = Math.Max(floor, dataMin - 2);
        var max = Math.Min(ceiling, dataMax + 2);
        if (max - min < 1)
        {
            min = Math.Max(floor, min - 2);
            max = Math.Min(ceiling, max + 2);
        }

        var range = Math.Max(max - min, 0.001);
        DrawCore(canvas, values, width, height, min, range, strokeColor);
    }

    private static void DrawCore(Canvas canvas, IReadOnlyList<double> values, double width, double height, double min, double range, Color strokeColor)
    {

        var points = new PointCollection(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var x = i * width / (values.Count - 1);
            var normalized = Math.Clamp((values[i] - min) / range, 0, 1);
            var y = height - normalized * (height - 2) - 1;
            points.Add(new Point(x, y));
        }

        var fill = new Polygon
        {
            Fill = new SolidColorBrush(Color.FromArgb(40, strokeColor.R, strokeColor.G, strokeColor.B)),
            Points = new PointCollection(points)
        };
        fill.Points.Add(new Point(width, height));
        fill.Points.Add(new Point(0, height));
        canvas.Children.Add(fill);

        canvas.Children.Add(new Polyline
        {
            Stroke = new SolidColorBrush(strokeColor),
            StrokeThickness = 1.5,
            Points = points
        });
    }
}

