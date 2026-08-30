using System.IO;
using System.Text;
using Microsoft.Win32;

namespace KlarWin.Services;

public sealed class LicenseKeyEntry
{
    public string Product { get; init; } = "";
    public string Key { get; init; } = "";
    public string Source { get; init; } = "";
}

public sealed class LicenseKeyService
{
    public IReadOnlyList<LicenseKeyEntry> Collect()
    {
        var list = new List<LicenseKeyEntry>();
        TryAddWindows(list);
        TryAddOffice(list);
        TryAddCommonSoftware(list);
        return list;
    }

    public string ExportToFile(IEnumerable<LicenseKeyEntry> entries)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "KlarWin");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"software-keys-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        var sb = new StringBuilder();
        sb.AppendLine("KlarWin Software-Sicherung");
        sb.AppendLine($"Erstellt: {DateTime.Now:dd.MM.yyyy HH:mm}");
        sb.AppendLine("Nur für diesen PC. Schlüssel sicher aufbewahren.");
        sb.AppendLine(new string('-', 48));
        foreach (var entry in entries)
        {
            sb.AppendLine(entry.Product);
            sb.AppendLine(entry.Key);
            sb.AppendLine($"Quelle: {entry.Source}");
            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }

    private static void TryAddWindows(List<LicenseKeyEntry> list)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var digital = key?.GetValue("DigitalProductId") as byte[];
            var productName = key?.GetValue("ProductName")?.ToString() ?? "Windows";
            if (digital is null) return;
            var decoded = DecodeProductKey(digital);
            if (!string.IsNullOrWhiteSpace(decoded))
            {
                list.Add(new LicenseKeyEntry
                {
                    Product = productName,
                    Key = decoded,
                    Source = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
                });
            }
        }
        catch
        {
            // ignore
        }
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
                        list.Add(new LicenseKeyEntry
                        {
                            Product = name,
                            Key = decoded,
                            Source = $@"HKLM\{root}\{version}\Registration"
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

    private static void TryAddCommonSoftware(List<LicenseKeyEntry> list)
    {
        // Generic ProductID / Serial / LicenseKey values under uninstall and vendor keys.
        ScanForKeyValues(list, Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", 2);
        ScanForKeyValues(list, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", 2);
        ScanForKeyValues(list, Registry.CurrentUser, @"SOFTWARE", 3);
    }

    private static void ScanForKeyValues(List<LicenseKeyEntry> list, RegistryKey hive, string path, int depth)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            if (key is null) return;
            Walk(key, path, depth, list);
        }
        catch
        {
            // ignore
        }
    }

    private static void Walk(RegistryKey key, string path, int depth, List<LicenseKeyEntry> list)
    {
        string[] interesting = ["ProductID", "ProductId", "Serial", "SerialNumber", "LicenseKey", "RegistrationKey", "CDKey"];
        foreach (var name in key.GetValueNames())
        {
            if (!interesting.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            var value = key.GetValue(name)?.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(value) || value.Length < 5) continue;
            if (value.All(char.IsDigit) && value.Length < 8) continue;
            var product = key.GetValue("DisplayName")?.ToString()
                          ?? key.GetValue("ProductName")?.ToString()
                          ?? Path.GetFileName(path);
            if (list.Any(e => e.Key.Equals(value, StringComparison.OrdinalIgnoreCase) && e.Product.Equals(product, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            list.Add(new LicenseKeyEntry
            {
                Product = product ?? "Unbekannt",
                Key = value,
                Source = path + "\\" + name
            });
        }

        if (depth <= 0) return;
        foreach (var subName in key.GetSubKeyNames().Take(80))
        {
            try
            {
                using var sub = key.OpenSubKey(subName);
                if (sub is null) continue;
                Walk(sub, path + "\\" + subName, depth - 1, list);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static string DecodeProductKey(byte[] digitalProductId)
    {
        // Works for many older Windows/Office DigitalProductId blobs.
        if (digitalProductId.Length < 67) return "";
        const string digits = "BCDFGHJKMPQRTVWXY2346789";
        var isWin8OrNewer = (digitalProductId[66] / 6) & 1;
        digitalProductId[66] = (byte)((digitalProductId[66] & 0xF7) | ((isWin8OrNewer & 2) * 4));

        var keyChars = new char[29];
        var hexPid = new byte[15];
        Array.Copy(digitalProductId, 52, hexPid, 0, 15);

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
        if (isWin8OrNewer != 0)
        {
            // Win8+ keys often need the 'N' insertion variant; leave decoded string as-is when plausible.
        }

        return key.Contains('-', StringComparison.Ordinal) ? key : "";
    }
}
