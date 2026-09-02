using System.Management;
using Microsoft.Win32;

namespace KlarWin.Services;

public sealed class MachineInfoService
{
    public string BuildLicenseOwnershipText()
    {
        var created = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
        var machine = Environment.MachineName;
        var user = Environment.UserName;
        var domain = Environment.UserDomainName;
        var (manufacturer, model) = ReadSystemModel();
        var windows = ReadWindowsCaption();

        var modelLine = !string.IsNullOrWhiteSpace(manufacturer) || !string.IsNullOrWhiteSpace(model)
            ? $"{manufacturer} {model}".Trim()
            : "Modell nicht ermittelt";

        return
            $"Erstellt am {created} auf diesem Computer:\n" +
            $"• Computername (Windows): {machine}\n" +
            $"• Gerät: {modelLine}\n" +
            $"• Benutzer: {domain}\\{user}\n" +
            $"• Betriebssystem: {windows}\n\n" +
            "Diese Schlüssel gelten nur für das oben genannte Gerät. " +
            "Ausdruck oder PDF sicher aufbewahren — nicht weitergeben.";
    }

    public string BuildLicenseOwnershipHtml()
    {
        var text = BuildLicenseOwnershipText();
        return WebEncode(text).Replace("\n", "<br/>");
    }

    private static (string Manufacturer, string Model) ReadSystemModel()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem");
            foreach (var obj in searcher.Get().Cast<ManagementObject>())
            {
                var manufacturer = obj["Manufacturer"]?.ToString()?.Trim() ?? "";
                var model = obj["Model"]?.ToString()?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(manufacturer) || !string.IsNullOrWhiteSpace(model))
                {
                    return (manufacturer, model);
                }
            }
        }
        catch
        {
            // WMI optional
        }

        return ("", "");
    }

    private static string ReadWindowsCaption()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is null) return "Windows";
            var product = key.GetValue("ProductName")?.ToString()?.Trim() ?? "Windows";
            var display = key.GetValue("DisplayVersion")?.ToString()?.Trim();
            var build = key.GetValue("CurrentBuild")?.ToString()?.Trim();
            if (int.TryParse(build, out var buildNum) && buildNum >= 22000
                && product.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
            {
                product = product.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
            }

            var parts = new List<string> { product };
            if (!string.IsNullOrWhiteSpace(display)) parts.Add(display);
            if (!string.IsNullOrWhiteSpace(build)) parts.Add($"Build {build}");
            return string.Join(" · ", parts);
        }
        catch
        {
            return "Windows";
        }
    }

    private static string WebEncode(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
