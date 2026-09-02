using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KlarWin.Services;

public sealed class WindowsPcProfile
{
    public bool IsWindows11 { get; init; }
    public string ProductName { get; init; } = "Windows";
    public string EditionId { get; init; } = "";
    public string EditionLabel { get; init; } = "";
    public string DisplayVersion { get; init; } = "";
    public int Build { get; init; }
    public string Architecture { get; init; } = "x64";
    public string InstallLanguage { get; init; } = "de-DE";
    public string LanguageLabel { get; init; } = "Deutsch";
    public string FidoLanguageQuery { get; init; } = "German";
    public bool PreferChinaMedia { get; init; }
}

public static class WindowsPcProfileService
{
    public static WindowsPcProfile Read()
    {
        var build = 0;
        var productName = "Windows";
        var displayVersion = "";
        var editionId = "";
        var installLanguage = CultureInfo.CurrentUICulture.Name;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is not null)
            {
                productName = key.GetValue("ProductName")?.ToString()?.Trim() ?? productName;
                displayVersion = key.GetValue("DisplayVersion")?.ToString()?.Trim() ?? "";
                editionId = key.GetValue("EditionID")?.ToString()?.Trim() ?? "";
                if (int.TryParse(key.GetValue("CurrentBuild")?.ToString(), out var parsedBuild))
                {
                    build = parsedBuild;
                }

                var regLang = key.GetValue("InstallLanguage")?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(regLang))
                {
                    installLanguage = regLang.Length == 3
                        ? Iso639ToCulture(regLang)
                        : regLang;
                }
            }
        }
        catch
        {
            // registry optional
        }

        if (build >= 22000 && productName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
        {
            productName = productName.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
        }

        var culture = TryCulture(installLanguage);
        var languageLabel = culture?.NativeName.Split('(')[0].Trim() ?? installLanguage;
        var fidoLanguage = MapFidoLanguage(culture ?? CultureInfo.CurrentUICulture);
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "ARM64",
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            _ => Environment.Is64BitOperatingSystem ? "x64" : "x86"
        };

        var editionLabel = MapEditionLabel(editionId, productName);
        var preferChina = IsChinaMediaPreferred(installLanguage, culture);

        return new WindowsPcProfile
        {
            IsWindows11 = build >= 22000 || productName.Contains("Windows 11", StringComparison.OrdinalIgnoreCase),
            ProductName = productName,
            EditionId = editionId,
            EditionLabel = editionLabel,
            DisplayVersion = displayVersion,
            Build = build,
            Architecture = arch,
            InstallLanguage = installLanguage,
            LanguageLabel = languageLabel,
            FidoLanguageQuery = fidoLanguage,
            PreferChinaMedia = preferChina
        };
    }

    private static bool IsChinaMediaPreferred(string installLanguage, CultureInfo? culture)
    {
        if (installLanguage.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase)) return true;
        return culture?.Name.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static CultureInfo? TryCulture(string name)
    {
        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch
        {
            return null;
        }
    }

    private static string Iso639ToCulture(string threeLetter)
    {
        try
        {
            var cultures = CultureInfo.GetCultures(CultureTypes.AllCultures);
            foreach (var culture in cultures)
            {
                if (culture.ThreeLetterISOLanguageName.Equals(threeLetter, StringComparison.OrdinalIgnoreCase))
                {
                    return culture.Name;
                }
            }
        }
        catch
        {
            // ignore
        }

        return threeLetter;
    }

    private static string MapFidoLanguage(CultureInfo culture)
    {
        if (culture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase)) return "German";
        if (culture.Name.Equals("en-US", StringComparison.OrdinalIgnoreCase)) return "English";
        if (culture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase)) return "English";
        if (culture.TwoLetterISOLanguageName.Equals("fr", StringComparison.OrdinalIgnoreCase)) return "French";
        if (culture.TwoLetterISOLanguageName.Equals("es", StringComparison.OrdinalIgnoreCase)) return "Spanish";
        if (culture.TwoLetterISOLanguageName.Equals("it", StringComparison.OrdinalIgnoreCase)) return "Italian";
        if (culture.TwoLetterISOLanguageName.Equals("nl", StringComparison.OrdinalIgnoreCase)) return "Dutch";
        if (culture.TwoLetterISOLanguageName.Equals("pl", StringComparison.OrdinalIgnoreCase)) return "Polish";
        if (culture.Name.Equals("pt-BR", StringComparison.OrdinalIgnoreCase)) return "Brazil";
        if (culture.TwoLetterISOLanguageName.Equals("pt", StringComparison.OrdinalIgnoreCase)) return "Portuguese";
        if (culture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase)) return "Russian";
        if (culture.TwoLetterISOLanguageName.Equals("ja", StringComparison.OrdinalIgnoreCase)) return "Japanese";
        if (culture.TwoLetterISOLanguageName.Equals("ko", StringComparison.OrdinalIgnoreCase)) return "Korean";
        if (culture.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)) return "Chinese";
        if (culture.Name.Equals("zh-TW", StringComparison.OrdinalIgnoreCase)) return "Chinese";
        return culture.DisplayName;
    }

    private static string MapEditionLabel(string editionId, string productName)
    {
        var mapped = editionId switch
        {
            "Core" => "Home",
            "CoreSingleLanguage" => "Home (Single Language)",
            "CoreCountrySpecific" => "Home",
            "Professional" => "Pro",
            "ProfessionalWorkstation" => "Pro for Workstations",
            "ProfessionalEducation" => "Pro Education",
            "Enterprise" => "Enterprise",
            "EnterpriseS" => "Enterprise S",
            "Education" => "Education",
            "IoTEnterprise" => "IoT Enterprise",
            _ => string.IsNullOrWhiteSpace(editionId) ? "Unbekannt" : editionId
        };

        if (productName.Contains(mapped, StringComparison.OrdinalIgnoreCase)) return mapped;
        return string.IsNullOrWhiteSpace(mapped) ? productName : mapped;
    }
}
