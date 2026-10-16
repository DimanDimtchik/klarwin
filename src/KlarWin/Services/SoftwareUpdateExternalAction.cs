using System.Diagnostics;

namespace KlarWin.Services;

public enum SoftwareUpdateExternalKind
{
    None,
    MicrosoftStore,
    WindowsUpdate
}

/// <summary>
/// Alternative update path when winget cannot (or should not) install the package.
/// </summary>
public sealed class SoftwareUpdateExternalAction
{
    public string PackageName { get; init; } = "";
    public string PackageId { get; init; } = "";
    public SoftwareUpdateExternalKind Kind { get; init; }
    public string ButtonLabel { get; init; } = "";
    public string Uri { get; init; } = "";
    public string Hint { get; init; } = "";

    public static SoftwareUpdateExternalAction? TryForItem(SoftwareUpdateItem item)
    {
        if (item.Source.Equals("wu-driver", StringComparison.OrdinalIgnoreCase)
            || item.Kind.Equals("Treiber", StringComparison.OrdinalIgnoreCase))
        {
            return WindowsUpdate(item.Name, item.Id,
                "Treiber-Update über Windows Update.");
        }

        if (item.PackageSource.Equals("msstore", StringComparison.OrdinalIgnoreCase)
            || LooksLikeMsStoreProductId(item.Id))
        {
            return MicrosoftStore(item.Name, item.Id);
        }

        if (IsKnownWindowsUpdateApp(item))
        {
            return WindowsUpdate(item.Name, item.Id,
                "Update läuft zuverlässiger über Windows Update (nicht über winget).");
        }

        if (IsKnownMicrosoftStoreApp(item))
        {
            return MicrosoftStore(item.Name, item.Id,
                "Update läuft über den Microsoft Store.");
        }

        return null;
    }

    public static SoftwareUpdateExternalAction? TryFromInstallHint(SoftwareUpdateItem item, string hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return null;

        if (hint.Contains("Installationstechnologie", StringComparison.OrdinalIgnoreCase)
            || hint.Contains("nicht über winget aktualisierbar", StringComparison.OrdinalIgnoreCase))
        {
            return TryForItem(item)
                   ?? WindowsUpdate(item.Name, item.Id,
                       "winget kann dieses Paket nicht aktualisieren — Windows Update oder Store nutzen.");
        }

        if (hint.Contains("msstore", StringComparison.OrdinalIgnoreCase)
            || hint.Contains("Microsoft Store", StringComparison.OrdinalIgnoreCase))
        {
            return MicrosoftStore(item.Name, item.Id);
        }

        return null;
    }

    public static SoftwareUpdateExternalAction MicrosoftStore(
        string name,
        string id,
        string? hint = null)
    {
        var uri = LooksLikeMsStoreProductId(id)
            ? $"ms-windows-store://pdp/?ProductId={Uri.EscapeDataString(id)}"
            : $"ms-windows-store://search/?query={Uri.EscapeDataString(string.IsNullOrWhiteSpace(name) ? id : name)}";

        return new SoftwareUpdateExternalAction
        {
            PackageName = name,
            PackageId = id,
            Kind = SoftwareUpdateExternalKind.MicrosoftStore,
            ButtonLabel = "Microsoft Store öffnen",
            Uri = uri,
            Hint = hint ?? "Update über den Microsoft Store."
        };
    }

    public static SoftwareUpdateExternalAction WindowsUpdate(
        string name,
        string id,
        string? hint = null) =>
        new()
        {
            PackageName = name,
            PackageId = id,
            Kind = SoftwareUpdateExternalKind.WindowsUpdate,
            ButtonLabel = "Windows Update öffnen",
            Uri = "ms-settings:windowsupdate",
            Hint = hint ?? "Update über Windows Update."
        };

    public static bool LooksLikeMsStoreProductId(string id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length is >= 10 and <= 16
        && id[0] is '9' or 'X'
        && id.All(c => char.IsLetterOrDigit(c));

    private static bool IsKnownWindowsUpdateApp(SoftwareUpdateItem item)
    {
        var id = item.Id ?? "";
        var name = item.Name ?? "";
        if (id.Equals("Microsoft.Edge", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.Contains("Microsoft.Edge", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("Microsoft Edge", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.Contains("Visual.C++", StringComparison.OrdinalIgnoreCase)
            && id.Contains("Redist", StringComparison.OrdinalIgnoreCase))
            return false; // VC++ is winget/admin, not WU UI
        return false;
    }

    private static bool IsKnownMicrosoftStoreApp(SoftwareUpdateItem item)
    {
        var id = item.Id ?? "";
        var name = item.Name ?? "";
        if (name.Contains("Copilot", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.Contains("Copilot", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("Outlook for Windows", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.Contains("OutlookForWindows", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public bool TryOpen()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Uri,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
