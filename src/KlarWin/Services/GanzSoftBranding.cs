using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KlarWin.Services;

public static class GanzSoftBranding
{
    public const string KlarWinIconResource = "pack://application:,,,/Assets/klarwin-icon.png";
    public const string IconResource = "pack://application:,,,/Assets/ganzsoft-icon.png";
    public const string WordmarkResource = "pack://application:,,,/Assets/ganzsoft-wordmark.png";

    /// <summary>Mediathek-IDs auf ganz-soft.de (Einstellungen → Schriften &amp; Logo).</summary>
    public const string MediathekFaviconId = "20260828174237_fca321";
    public const string MediathekLogoId = "20260828174130_631472";

    public static ImageSource LoadPromoBrand() => LoadWordmark();

    public static ImageSource LoadKlarWinIcon()
    {
        var disk = TryLoadFromDisk("klarwin-icon.png");
        if (disk is not null) return disk;
        if (ResourceExists(KlarWinIconResource)) return LoadBitmap(KlarWinIconResource);
        return LoadBestIcon();
    }

    public static ImageSource LoadIcon() => LoadBestIcon();

    public static ImageSource LoadWordmark()
    {
        var wordmark = TryLoadFromDisk("ganzsoft-wordmark.png");
        if (wordmark is not null) return wordmark;
        wordmark = TryLoadResource("ganzsoft-wordmark.png");
        if (wordmark is not null) return wordmark;
        return LoadBestIcon();
    }

    private static ImageSource LoadBestIcon()
    {
        var disk = TryLoadFromDisk("ganzsoft-icon.png");
        if (disk is not null) return disk;
        return Load(IconResource);
    }

    private static BitmapImage? TryLoadResource(string fileName) =>
        fileName switch
        {
            "ganzsoft-wordmark.png" when ResourceExists(WordmarkResource) => LoadBitmap(WordmarkResource),
            _ => null
        };

    private static bool ResourceExists(string packUri)
    {
        try
        {
            var stream = System.Windows.Application.GetResourceStream(new Uri(packUri, UriKind.Absolute));
            return stream is not null;
        }
        catch
        {
            return false;
        }
    }

    private static BitmapImage LoadBitmap(string packUri)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(packUri, UriKind.Absolute);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static ImageSource Load(string packUri) => LoadBitmap(packUri);

    private static BitmapImage? TryLoadFromDisk(string fileName)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
            if (!File.Exists(path) || new FileInfo(path).Length < 4096) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
