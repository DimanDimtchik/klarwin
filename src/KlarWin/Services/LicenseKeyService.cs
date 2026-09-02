using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace KlarWin.Services;

public sealed class LicenseKeyEntry
{
    public string Product { get; init; } = "";
    public string Key { get; init; } = "";
    public string Source { get; init; } = "";
    public string Kind { get; init; } = "Product Key";
}

public sealed class LicenseKeyService
{
    private static readonly Regex ProductKeyPattern = new(@"^[A-Z0-9]{5}(-[A-Z0-9]{5}){4}$", RegexOptions.Compiled);
    private static readonly Regex JunkNamePattern = new(@"(?i)(hotfix|kb\d{6,7}|update for|security update|rollup|language pack|proofing|redistributable|runtime|driver|printer driver)", RegexOptions.Compiled);
    private readonly MachineInfoService _machineInfo = new();

    public IReadOnlyList<LicenseKeyEntry> Collect()
    {
        var list = new List<LicenseKeyEntry>();
        TryAddWindows(list);
        TryAddOffice(list);
        TryAddUninstallKeys(list);
        return list;
    }

    public string ExportToFile(IEnumerable<LicenseKeyEntry> entries)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "KlarWin");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"software-keys-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        var sb = new StringBuilder();
        sb.AppendLine("KlarWin Software-Sicherung");
        sb.AppendLine(_machineInfo.BuildLicenseOwnershipText());
        sb.AppendLine(new string('-', 48));
        foreach (var entry in entries)
        {
            sb.AppendLine(entry.Product);
            sb.AppendLine(entry.Key);
            sb.AppendLine($"{entry.Kind} · Quelle: {entry.Source}");
            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }

    public string ExportPrintableHtml(IEnumerable<LicenseKeyEntry> entries)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "KlarWin");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"software-keys-{DateTime.Now:yyyyMMdd-HHmmss}.html");
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"de\"><head><meta charset=\"utf-8\"/>");
        sb.AppendLine("<title>KlarWin Software-Keys</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#18241f}");
        sb.AppendLine("h1{font-size:22px} table{border-collapse:collapse;width:100%;margin-top:16px}");
        sb.AppendLine("th,td{border:1px solid #c3d1c8;padding:10px;text-align:left;vertical-align:top}");
        sb.AppendLine("th{background:#eef3ef} .key{font-family:Consolas,monospace;font-size:14px;word-break:break-all}");
        sb.AppendLine("@media print{body{margin:12mm}}</style></head><body>");
        sb.AppendLine("<h1>KlarWin — Software-Keys</h1>");
        sb.AppendLine($"<p>{_machineInfo.BuildLicenseOwnershipHtml()}</p>");
        sb.AppendLine("<table><thead><tr><th>Programm</th><th>Schlüssel</th><th>Art</th></tr></thead><tbody>");
        foreach (var entry in entries)
        {
            sb.AppendLine("<tr>");
            sb.AppendLine($"<td>{WebEncode(entry.Product)}</td>");
            sb.AppendLine($"<td class=\"key\">{WebEncode(entry.Key)}</td>");
            sb.AppendLine($"<td>{WebEncode(entry.Kind)}</td>");
            sb.AppendLine("</tr>");
        }

        sb.AppendLine("</tbody></table></body></html>");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }

    private static string WebEncode(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static void TryAddWindows(List<LicenseKeyEntry> list)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is null) return;
            var digital = key.GetValue("DigitalProductId") as byte[];
            if (digital is null) return;
            var decoded = DecodeProductKey(digital);
            if (string.IsNullOrWhiteSpace(decoded)) return;

            var caption = ResolveWindowsCaption(key);
            list.Add(new LicenseKeyEntry
            {
                Product = caption,
                Key = decoded,
                Source = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                Kind = "Windows Product Key (OEM/Retail)"
            });
        }
        catch
        {
            // ignore
        }
    }

    private static string ResolveWindowsCaption(RegistryKey key)
    {
        var build = int.TryParse(key.GetValue("CurrentBuild")?.ToString(), out var b) ? b : 0;
        var productName = key.GetValue("ProductName")?.ToString() ?? "Windows";
        var displayVersion = key.GetValue("DisplayVersion")?.ToString();
        var edition = key.GetValue("EditionID")?.ToString();

        if (build >= 22000 && productName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
        {
            productName = productName.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
        }

        var parts = new List<string> { productName.Trim() };
        if (!string.IsNullOrWhiteSpace(displayVersion)) parts.Add(displayVersion);
        if (!string.IsNullOrWhiteSpace(edition) && !productName.Contains(edition, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(edition);
        }

        return string.Join(" · ", parts);
    }

    private static void TryAddOffice(List<LicenseKeyEntry> list)
    {
        string[] roots =
        [
            @"SOFTWARE\Microsoft\Office",
            @"SOFTWARE\WOW6432Node\Microsoft\Office"
        ];

        foreach (var root in roots)
        {
            try
            {
                using var office = Registry.LocalMachine.OpenSubKey(root);
                if (office is null) continue;
                foreach (var version in office.GetSubKeyNames())
                {
                    using var reg = office.OpenSubKey($@"{version}\Registration");
                    if (reg is null) continue;
                    foreach (var productGuid in reg.GetSubKeyNames())
                    {
                        using var product = reg.OpenSubKey(productGuid);
                        var digital = product?.GetValue("DigitalProductId") as byte[];
                        var name = product?.GetValue("ProductName")?.ToString()
                                   ?? product?.GetValue("ConvertToEdition")?.ToString()
                                   ?? $"Office {version}";
                        if (digital is null) continue;
                        var decoded = DecodeProductKey(digital);
                        if (string.IsNullOrWhiteSpace(decoded)) continue;
                        if (list.Any(e => e.Key.Equals(decoded, StringComparison.OrdinalIgnoreCase))) continue;
                        list.Add(new LicenseKeyEntry
                        {
                            Product = name,
                            Key = decoded,
                            Source = $@"HKLM\{root}\{version}\Registration",
                            Kind = "Office Product Key"
                        });
                    }
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void TryAddUninstallKeys(List<LicenseKeyEntry> list)
    {
        ScanUninstall(list, Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        ScanUninstall(list, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
    }

    private static void ScanUninstall(List<LicenseKeyEntry> list, RegistryKey hive, string path)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            if (key is null) return;
            foreach (var subName in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(subName);
                if (sub is null) continue;
                var displayName = sub.GetValue("DisplayName")?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(displayName) || JunkNamePattern.IsMatch(displayName)) continue;

                foreach (var valueName in new[] { "ProductID", "ProductId", "SerialNumber" })
                {
                    var value = sub.GetValue(valueName)?.ToString()?.Trim();
                    if (!LooksLikeSoftwareKey(value, displayName)) continue;
                    if (list.Any(e => e.Key.Equals(value, StringComparison.OrdinalIgnoreCase))) continue;
                    list.Add(new LicenseKeyEntry
                    {
                        Product = displayName,
                        Key = value!,
                        Source = $@"{path}\{subName}\{valueName}",
                        Kind = valueName.Contains("Serial", StringComparison.OrdinalIgnoreCase) ? "Seriennummer" : "Product ID"
                    });
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    private static bool LooksLikeSoftwareKey(string? value, string productName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 8) return false;
        if (value.All(char.IsDigit) && value.Length <= 12) return false;
        if (JunkNamePattern.IsMatch(productName)) return false;
        if (value.StartsWith("KB", StringComparison.OrdinalIgnoreCase)) return false;
        if (ProductKeyPattern.IsMatch(value.Replace(' ', '-'))) return true;
        if (value.Contains('-') && value.Length >= 14) return true;
        return value.Length >= 20 && value.Any(char.IsLetter);
    }

    private static string DecodeProductKey(byte[] digitalProductId)
    {
        if (digitalProductId.Length < 67) return "";
        var data = (byte[])digitalProductId.Clone();
        const string digits = "BCDFGHJKMPQRTVWXY2346789";
        var isWin8OrNewer = (data[66] / 6) & 1;
        data[66] = (byte)((data[66] & 0xF7) | ((isWin8OrNewer & 2) * 4));

        var keyChars = new char[29];
        var hexPid = new byte[15];
        Array.Copy(data, 52, hexPid, 0, 15);

        for (var i = 28; i >= 0; i--)
        {
            if ((i + 1) % 6 == 0)
            {
                keyChars[i] = '-';
                continue;
            }

            var digitMapIndex = 0;
            for (var j = 14; j >= 0; j--)
            {
                var byteValue = (digitMapIndex << 8) | hexPid[j];
                hexPid[j] = (byte)(byteValue / 24);
                digitMapIndex = byteValue % 24;
                keyChars[i] = digits[digitMapIndex];
            }
        }

        var key = new string(keyChars);
        return key.Contains('-', StringComparison.Ordinal) ? key : "";
    }
}
