using System.Text;

namespace KlarWin.Services;

public sealed class WindowsIsoRecommendation
{
    public WindowsPcProfile Profile { get; init; } = new();
    public string IsoTitle { get; init; } = "";
    public string SetupHint { get; init; } = "";
    public string SummaryLine { get; init; } = "";
    public string FidoWindowsArg { get; init; } = "11";
    public string FidoEditionArg { get; init; } = "Home/Pro";
    public string FidoLanguageArg { get; init; } = "German";
    public string FidoArchitectureArg { get; init; } = "x64";
}

public static class WindowsIsoRecommendationService
{
    public static WindowsIsoRecommendation Build(WindowsPcProfile profile)
    {
        var winArg = profile.IsWindows11 ? "11" : "10";
        var editionArg = profile.PreferChinaMedia
            ? "China"
            : "Home/Pro";
        var isoEditionLabel = profile.PreferChinaMedia
            ? "Windows 11 Home China" // Fido picks exact China SKU
            : profile.IsWindows11
                ? "Windows 11 Home/Pro/Edu (Multi-Edition-ISO)"
                : "Windows 10 Home/Pro/Edu (Multi-Edition-ISO)";

        var versionPart = string.IsNullOrWhiteSpace(profile.DisplayVersion)
            ? "neueste Version"
            : $"neueste Version (Ihr PC: {profile.DisplayVersion}, Build {profile.Build})";

        var isoTitle =
            $"{(profile.IsWindows11 ? "Windows 11" : "Windows 10")} · {isoEditionLabel} · " +
            $"{profile.Architecture} · {profile.LanguageLabel} · {versionPart}";

        var setupHint =
            $"Bei der Neuinstallation die Edition „{profile.EditionLabel}“ wählen — " +
            "auf diesem Gerät ist in der Regel eine digitale Lizenz hinterlegt (Reaktivierung nach Setup).";

        if (profile.EditionId.Contains("Enterprise", StringComparison.OrdinalIgnoreCase)
            || profile.EditionId.Contains("Education", StringComparison.OrdinalIgnoreCase))
        {
            setupHint =
                $"Bei der Neuinstallation passende Edition wählen (dieser PC: {profile.EditionLabel}). " +
                "Enterprise/Education erfordert ggf. Volume-Lizenz oder Schul-/Firmenzugang.";
        }

        var summaryLine =
            $"{profile.ProductName} · {profile.EditionLabel} · {profile.Architecture} · {profile.LanguageLabel}";

        return new WindowsIsoRecommendation
        {
            Profile = profile,
            IsoTitle = isoTitle,
            SetupHint = setupHint,
            SummaryLine = summaryLine,
            FidoWindowsArg = winArg,
            FidoEditionArg = editionArg,
            FidoLanguageArg = profile.FidoLanguageQuery,
            FidoArchitectureArg = profile.Architecture
        };
    }

    public static string FormatPanelText(WindowsIsoRecommendation recommendation)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Empfehlung für DIESEN PC (automatisch aus Windows-Daten):");
        sb.AppendLine();
        sb.AppendLine($"• Installiert: {recommendation.SummaryLine}");
        sb.AppendLine($"• Passende ISO: {recommendation.IsoTitle}");
        sb.AppendLine($"• {recommendation.SetupHint}");
        sb.AppendLine();
        sb.AppendLine("„ISO automatisch laden“ holt die offizielle Datei direkt von Microsoft-Servern — ohne Download-Seite und ohne Varianten-Rätsel.");
        return sb.ToString().TrimEnd();
    }

    public static string SuggestFileName(WindowsIsoRecommendation recommendation)
    {
        var win = recommendation.Profile.IsWindows11 ? "Win11" : "Win10";
        var lang = recommendation.Profile.InstallLanguage.Replace('-', '_');
        return $"KlarWin_{win}_{recommendation.Profile.Architecture}_{lang}.iso";
    }
}
