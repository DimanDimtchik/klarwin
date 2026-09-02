using System.Net.Http;
using System.Text.RegularExpressions;

namespace KlarWin.Services;

/// <summary>
/// Resolves MAC → manufacturer via OUI (first 3 bytes). Detects privacy/random MACs.
/// Optional online lookup with local cache for unknown prefixes.
/// </summary>
public static class MacVendorLookup
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private static readonly object CacheLock = new();

    private static readonly Dictionary<string, string> Vendors = new(StringComparer.OrdinalIgnoreCase)
    {
        // Apple (common)
        ["001B63"] = "Apple", ["001E65"] = "Apple", ["002312"] = "Apple", ["002500"] = "Apple",
        ["002608"] = "Apple", ["0026BB"] = "Apple", ["002713"] = "Apple", ["003065"] = "Apple",
        ["040CCE"] = "Apple", ["041E64"] = "Apple", ["049FCA"] = "Apple", ["04D3CF"] = "Apple",
        ["0C7485"] = "Apple", ["0CBC9A"] = "Apple", ["109ADD"] = "Apple", ["14109F"] = "Apple",
        ["148FC6"] = "Apple", ["18AF61"] = "Apple", ["1C1AC0"] = "Apple", ["1CE62B"] = "Apple",
        ["20A2E4"] = "Apple", ["24A2E1"] = "Apple", ["28CFDA"] = "Apple", ["28E02C"] = "Apple",
        ["2C1F23"] = "Apple", ["2CF0A2"] = "Apple", ["34A395"] = "Apple", ["34C059"] = "Apple",
        ["34E894"] = "Apple", ["38F9D3"] = "Apple", ["3C0754"] = "Apple", ["3C22FB"] = "Apple",
        ["40A6D9"] = "Apple", ["40B395"] = "Apple", ["44D884"] = "Apple", ["48A91C"] = "Apple",
        ["48D705"] = "Apple", ["4C569D"] = "Apple", ["4CB910"] = "Apple", ["50BC96"] = "Apple",
        ["54AE27"] = "Apple", ["5CF938"] = "Apple", ["60F81D"] = "Apple", ["64B0A6"] = "Apple",
        ["64B473"] = "Apple", ["68A86D"] = "Apple", ["6C4A85"] = "Apple", ["6C8DC1"] = "Apple",
        ["70DEE2"] = "Apple", ["78A3E4"] = "Apple", ["7C6D62"] = "Apple", ["7CD1C3"] = "Apple",
        ["80E650"] = "Apple", ["88E87F"] = "Apple", ["8C8590"] = "Apple", ["8CFABA"] = "Apple",
        ["90E17B"] = "Apple", ["98D6BB"] = "Apple", ["9C207B"] = "Apple", ["A4C494"] = "Apple",
        ["A4D1D2"] = "Apple", ["AC1F74"] = "Apple", ["ACBC32"] = "Apple", ["B0BE76"] = "Apple",
        ["B48B19"] = "Apple", ["B8FF61"] = "Apple", ["BC3BAF"] = "Apple", ["BC4CC4"] = "Apple",
        ["BC926B"] = "Apple", ["C0B658"] = "Apple", ["C82A14"] = "Apple", ["CC08E0"] = "Apple",
        ["D0A637"] = "Apple", ["D023DB"] = "Apple", ["D8A25E"] = "Apple", ["DC2B61"] = "Apple",
        ["E0B9BA"] = "Apple", ["E0C767"] = "Apple", ["E4CE8F"] = "Apple", ["E80F67"] = "Apple",
        ["F0DBE2"] = "Apple", ["F0F61C"] = "Apple", ["F4F15A"] = "Apple", ["F8FFC2"] = "Apple",
        ["FCFC48"] = "Apple",

        // Samsung
        ["001632"] = "Samsung", ["0018AF"] = "Samsung", ["0021D1"] = "Samsung", ["0023D6"] = "Samsung",
        ["0024E9"] = "Samsung", ["002566"] = "Samsung", ["0026E8"] = "Samsung", ["18E829"] = "Samsung",
        ["20E874"] = "Samsung", ["34C3AC"] = "Samsung", ["38AA3C"] = "Samsung", ["40B0FA"] = "Samsung",
        ["5C497D"] = "Samsung", ["5C628B"] = "Samsung", ["78D75F"] = "Samsung", ["8C7712"] = "Samsung",
        ["A00798"] = "Samsung", ["C8D0E3"] = "Samsung", ["D85B2A"] = "Samsung", ["E4B318"] = "Samsung",
        ["F47B5E"] = "Samsung", ["FC0319"] = "Samsung",

        // Google / Nest
        ["001A11"] = "Google", ["3C5AB4"] = "Google", ["54EE75"] = "Google", ["94EB2C"] = "Google",
        ["F4F5D8"] = "Google", ["F4F5E8"] = "Google", ["546009"] = "Google",

        // Microsoft / Xbox
        ["0050F2"] = "Microsoft", ["001DD8"] = "Microsoft", ["7C1E52"] = "Microsoft",
        ["C83DC8"] = "Microsoft", ["EC59E7"] = "Microsoft",

        // Intel / Realtek / Broadcom (often PC/Wi‑Fi)
        ["001B21"] = "Intel", ["001E67"] = "Intel", ["0022FA"] = "Intel", ["0024D7"] = "Intel",
        ["3C970E"] = "Intel", ["7C7635"] = "Intel", ["84A423"] = "Intel", ["94E6F7"] = "Intel",
        ["A0A8CD"] = "Intel", ["DC41A9"] = "Intel", ["001E4C"] = "Realtek", ["00E04C"] = "Realtek",
        ["525400"] = "QEMU/KVM", ["000C29"] = "VMware", ["005056"] = "VMware",
        ["080027"] = "VirtualBox",

        // ASUS / TP-Link / Netgear / AVM
        ["10BF48"] = "ASUSTek", ["1C872C"] = "ASUSTek", ["2C4D54"] = "ASUSTek", ["2C56DC"] = "ASUSTek",
        ["60A44C"] = "ASUSTek", ["88D7F6"] = "ASUSTek", ["D05099"] = "ASUSTek", ["F832E4"] = "ASUSTek",
        ["14CC20"] = "TP-Link", ["1C61B4"] = "TP-Link", ["1C3BF3"] = "TP-Link", ["30B49E"] = "TP-Link",
        ["50C7BF"] = "TP-Link", ["C0C9E3"] = "TP-Link", ["744401"] = "Netgear", ["A040A0"] = "Netgear",
        ["9C3DCF"] = "Netgear", ["24A43C"] = "Amazon", ["0C47C9"] = "Amazon", ["74C246"] = "Amazon",
        ["9C0141"] = "AVM Fritz!Box", ["C0C1C0"] = "AVM Fritz!Box", ["2C91AB"] = "AVM Fritz!Box",

        // Xiaomi / Huawei / OnePlus / Oppo
        ["34CE00"] = "Xiaomi", ["64CC2E"] = "Xiaomi", ["78F2A0"] = "Xiaomi", ["98DA92"] = "Xiaomi",
        ["F0B429"] = "Xiaomi", ["00E0FC"] = "Huawei", ["04BD70"] = "Huawei", ["0C37DC"] = "Huawei",
        ["48DB50"] = "Huawei", ["AC853D"] = "Huawei", ["C8D15E"] = "Huawei",
        ["64A2F9"] = "OnePlus", ["C0EEFB"] = "OnePlus", ["A091A2"] = "OnePlus",

        // LG / Sony / Philips / Bose
        ["001E75"] = "LG Electronics", ["504F94"] = "LG Electronics", ["54E1AD"] = "LG Electronics",
        ["001A80"] = "Sony", ["001DAB"] = "Sony", ["00248D"] = "Sony", ["54489A"] = "Sony",
        ["001D19"] = "Philips", ["001D60"] = "Philips", ["000C8A"] = "Bose", ["08DF1F"] = "Bose",

        // Printers / peripherals
        ["001A2F"] = "Epson", ["00E018"] = "Epson", ["001BA9"] = "Brother", ["00E0BB"] = "Canon",
        ["0024A5"] = "HP", ["3C4A92"] = "HP", ["9457A5"] = "HP", ["001E0B"] = "Hewlett Packard",

        // Raspberry Pi / IoT
        ["B827EB"] = "Raspberry Pi", ["DCA632"] = "Raspberry Pi", ["E45F01"] = "Raspberry Pi",
        ["78E103"] = "Raspberry Pi", ["D83ADD"] = "Raspberry Pi",

        // Misc
        ["001A2B"] = "Ayecom Technology", ["0C54A5"] = "Pegatron", ["28C2DD"] = "AzureWave",
        ["7085C2"] = "ASRock", ["001DD8"] = "Microsoft"
    };

    public sealed class MacVendorResult
    {
        public string Display { get; init; } = "";
        public string Vendor { get; init; } = "";
        public bool IsRandomized { get; init; }
        public bool FromCache { get; init; }
    }

    public static string NormalizeMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return "";
        var hex = new string(mac.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
        return hex.Length >= 12 ? hex[..12] : hex;
    }

    /// <summary>IEEE locally administered bit (2nd least significant bit of first octet) → privacy MAC.</summary>
    public static bool IsRandomizedMac(string? mac)
    {
        var hex = NormalizeMac(mac);
        if (hex.Length < 2) return false;
        if (!byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var first))
        {
            return false;
        }

        return (first & 0x02) != 0;
    }

    public static string Lookup(string? mac)
    {
        var result = ResolveLocal(mac);
        return result.Vendor;
    }

    public static MacVendorResult ResolveLocal(string? mac, IReadOnlyDictionary<string, string>? learnedCache = null)
    {
        var hex = NormalizeMac(mac);
        if (hex.Length < 6)
        {
            return new MacVendorResult { Display = "keine MAC", Vendor = "" };
        }

        if (IsRandomizedMac(hex))
        {
            return new MacVendorResult
            {
                Display = "Zufällige MAC (Datenschutz)",
                Vendor = "",
                IsRandomized = true
            };
        }

        var prefix = hex[..6];
        if (Vendors.TryGetValue(prefix, out var known))
        {
            return new MacVendorResult { Display = known, Vendor = known };
        }

        if (learnedCache is not null && learnedCache.TryGetValue(prefix, out var cached) && !string.IsNullOrWhiteSpace(cached))
        {
            return new MacVendorResult { Display = cached, Vendor = cached, FromCache = true };
        }

        return new MacVendorResult { Display = "Hersteller unbekannt", Vendor = "" };
    }

    public static async Task<MacVendorResult> ResolveAsync(
        string? mac,
        Dictionary<string, string> learnedCache,
        CancellationToken cancellationToken = default)
    {
        var local = ResolveLocal(mac, learnedCache);
        if (local.IsRandomized || !string.IsNullOrWhiteSpace(local.Vendor))
        {
            return local;
        }

        var hex = NormalizeMac(mac);
        if (hex.Length < 6) return local;
        var prefix = hex[..6];

        try
        {
            var formatted = string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
            using var response = await Http.GetAsync($"https://api.macvendors.com/{formatted}", cancellationToken);
            if (!response.IsSuccessStatusCode) return local;
            var text = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            if (string.IsNullOrWhiteSpace(text) || text.Contains("Not Found", StringComparison.OrdinalIgnoreCase))
            {
                return local;
            }

            text = Regex.Replace(text, @"\s+", " ");
            if (text.Length > 60) text = text[..57] + "…";
            lock (CacheLock)
            {
                learnedCache[prefix] = text;
            }

            return new MacVendorResult { Display = text, Vendor = text, FromCache = true };
        }
        catch
        {
            return local;
        }
    }
}
