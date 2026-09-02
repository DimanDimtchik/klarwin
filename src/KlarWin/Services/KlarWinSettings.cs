using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KlarWin.Services;

public sealed class KlarWinSettings
{
    public bool HideShortcutArrows { get; set; }
    public bool ShowDesktopIcon { get; set; } = true;
    public Dictionary<string, AutostartBackupEntry> AutostartBackup { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> NetworkDeviceNotes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> LearnedMacVendors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Appearance
    public string BackgroundMode { get; set; } = "Gradient"; // Gradient | Solid | Image
    public string BackgroundColor1 { get; set; } = "#C5D4C8";
    public string BackgroundColor2 { get; set; } = "#E7EFE8";
    public string BackgroundColor3 { get; set; } = "#B7C9B6";
    public string BackgroundImagePath { get; set; } = "";
    public string FontFamilyUi { get; set; } = "Sitka Text";
    public string FontFamilyTitle { get; set; } = "Bahnschrift";
    public double FontSizeBody { get; set; } = 15;
    public double FontSizeTitle { get; set; } = 64;
    public string InkColor { get; set; } = "#18241F";
    public string MuteColor { get; set; } = "#4A5C54";
    public string AccentColor { get; set; } = "#8C6239";
    public string LeafColor { get; set; } = "#2F6A4A";
    public double TileCornerRadius { get; set; } = 0;
    public string TileBorderColor { get; set; } = "#C3D1C8";
    public double TileBorderThickness { get; set; } = 1;
    public string TileBackgroundColor { get; set; } = "#F4F8F5";
    public string TileHoverBorderColor { get; set; } = "#8C6239";

    // Promo / ads
    public bool ShowPromoBanner { get; set; } = true;
    public string PromoPosition { get; set; } = "Bottom"; // Bottom | Left | Right

    private static string PathFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KlarWin", "settings.json");

    public static KlarWinSettings Load()
    {
        try
        {
            var path = PathFile;
            if (!File.Exists(path)) return new KlarWinSettings();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<KlarWinSettings>(json) ?? new KlarWinSettings();
        }
        catch
        {
            return new KlarWinSettings();
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(PathFile)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(PathFile, json);
    }

    public static Color ParseColor(string? hex, Color fallback)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hex)) return fallback;
            return (Color)ColorConverter.ConvertFromString(hex.Trim())!;
        }
        catch
        {
            return fallback;
        }
    }

    public Brush CreateWindowBackground()
    {
        var mode = BackgroundMode?.Trim() ?? "Gradient";
        if (mode.Equals("Image", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(BackgroundImagePath)
            && File.Exists(BackgroundImagePath))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(BackgroundImagePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                return new ImageBrush(bitmap)
                {
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center
                };
            }
            catch
            {
                // fall through
            }
        }

        if (mode.Equals("Solid", StringComparison.OrdinalIgnoreCase))
        {
            return new SolidColorBrush(ParseColor(BackgroundColor1, Color.FromRgb(0xC5, 0xD4, 0xC8)));
        }

        return new LinearGradientBrush(
            new GradientStopCollection
            {
                new(ParseColor(BackgroundColor1, Color.FromRgb(0xC5, 0xD4, 0xC8)), 0),
                new(ParseColor(BackgroundColor2, Color.FromRgb(0xE7, 0xEF, 0xE8)), 0.45),
                new(ParseColor(BackgroundColor3, Color.FromRgb(0xB7, 0xC9, 0xB6)), 1)
            },
            new Point(0, 0),
            new Point(1, 1));
    }
}

public sealed class AutostartBackupEntry
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string Location { get; set; } = "";
    public string Hive { get; set; } = "";
}
