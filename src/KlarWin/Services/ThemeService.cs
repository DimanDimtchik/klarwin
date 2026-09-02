using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KlarWin.Services;

public static class ThemeService
{
    public static void Apply(Window window, KlarWinSettings settings)
    {
        window.Background = settings.CreateWindowBackground();
        window.FontFamily = new FontFamily(string.IsNullOrWhiteSpace(settings.FontFamilyUi) ? "Sitka Text" : settings.FontFamilyUi);
        window.Foreground = new SolidColorBrush(KlarWinSettings.ParseColor(settings.InkColor, Color.FromRgb(0x18, 0x24, 0x1F)));
        window.FontSize = Math.Clamp(settings.FontSizeBody, 11, 22);

        UpdateBrush("InkBrush", settings.InkColor, Color.FromRgb(0x18, 0x24, 0x1F));
        UpdateBrush("MuteBrush", settings.MuteColor, Color.FromRgb(0x4A, 0x5C, 0x54));
        UpdateBrush("BrassBrush", settings.AccentColor, Color.FromRgb(0x8C, 0x62, 0x39));
        UpdateBrush("LeafBrush", settings.LeafColor, Color.FromRgb(0x2F, 0x6A, 0x4A));

        Application.Current.Resources["TileButton"] = BuildTileStyle(settings);
        Application.Current.Resources["GhostButton"] = BuildGhostStyle(settings);
        Application.Current.Resources["ActionButton"] = BuildActionStyle(settings);

        if (window is MainWindow main)
        {
            main.ApplyChromeTheme(settings);
        }
    }

    private static void UpdateBrush(string key, string hex, Color fallback)
    {
        Application.Current.Resources[key] = new SolidColorBrush(KlarWinSettings.ParseColor(hex, fallback));
    }

    private static Style BuildTileStyle(KlarWinSettings settings)
    {
        var ink = KlarWinSettings.ParseColor(settings.InkColor, Color.FromRgb(0x18, 0x24, 0x1F));
        var tileBg = KlarWinSettings.ParseColor(settings.TileBackgroundColor, Color.FromRgb(0xF4, 0xF8, 0xF5));
        var border = KlarWinSettings.ParseColor(settings.TileBorderColor, Color.FromRgb(0xC3, 0xD1, 0xC8));
        var hover = KlarWinSettings.ParseColor(settings.TileHoverBorderColor, Color.FromRgb(0x8C, 0x62, 0x39));
        var radius = Math.Clamp(settings.TileCornerRadius, 0, 32);
        var thickness = Math.Clamp(settings.TileBorderThickness, 0, 8);

        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(ink)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(28, 26, 28, 26)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));

        var template = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.Name = "Surface";
        borderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(tileBg));
        borderFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(border));
        borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(thickness));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        borderFactory.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        borderFactory.AppendChild(content);
        template.VisualTree = borderFactory;

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(hover), "Surface"));
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xFB, 0xFD, 0xFA)), "Surface"));
        template.Triggers.Add(hoverTrigger);

        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static Style BuildActionStyle(KlarWinSettings settings)
    {
        var leaf = KlarWinSettings.ParseColor(settings.LeafColor, Color.FromRgb(0x2F, 0x6A, 0x4A));
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xF4, 0xF8, 0xF5))));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(leaf)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.FontFamilyProperty, new FontFamily(settings.FontFamilyTitle)));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 15.0));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(18, 10, 18, 10)));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand));

        var template = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        borderFactory.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(Math.Clamp(settings.TileCornerRadius, 0, 12)));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        borderFactory.AppendChild(content);
        template.VisualTree = borderFactory;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static Style BuildGhostStyle(KlarWinSettings settings)
    {
        var ink = KlarWinSettings.ParseColor(settings.InkColor, Color.FromRgb(0x18, 0x24, 0x1F));
        var accent = KlarWinSettings.ParseColor(settings.AccentColor, Color.FromRgb(0x8C, 0x62, 0x39));
        var style = new Style(typeof(Button), BuildActionStyle(settings));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(ink)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));

        var template = new ControlTemplate(typeof(Button));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(accent));
        borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(Math.Clamp(settings.TileCornerRadius, 0, 12)));
        borderFactory.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        borderFactory.AppendChild(content);
        template.VisualTree = borderFactory;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }
}
