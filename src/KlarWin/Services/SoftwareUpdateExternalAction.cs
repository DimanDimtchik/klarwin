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

    /// <summary>
    /// winget Id / common aliases → Microsoft Store ProductId (9…), so we open the
    /// correct Store page instead of a fuzzy search (e.g. „Copilot“ ≠ „Microsoft 365 Copilot“).
    /// </summary>
    private static readonly Dictionary<string, string> KnownStoreProductIds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Microsoft 365 Copilot (ex Office Hub) — NOT consumer „Microsoft Copilot“
            ["Microsoft.365Copilot"] = "9WZDNCRD29V9",
            ["9WZDNCRD29V9"] = "9WZDNCRD29V9",
            // Consumer Microsoft Copilot
            ["9NHT9RB2F4HD"] = "9NHT9RB2F4HD",
            // Outlook for Windows (new)
            ["9NRX3BBCDFS5"] = "9NRX3BBCDFS5",
            ["Microsoft.OutlookForWindows"] = "9NRX3BBCDFS5",
        };

    public static SoftwareUpdateExternalAction? TryForItem(SoftwareUpdateItem item)
    {
        if (item.Source.Equals("wu-driver", StringComparison.OrdinalIgnoreCase)
            || item.Kind.Equals("Treiber", StringComparison.OrdinalIgnoreCase))
        {
            return WindowsUpdate(item.Name, item.Id,
                "Treiber-Update über Windows Update.");
        }

        if (item.PackageSource.Equals("msstore", StringComparison.OrdinalIgnoreCase)
            || LooksLikeMsStoreProductId(item.Id)
            || TryResolveStoreProductId(item.Id) is not null)
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
                BuildStoreHint(item.Id, item.Name));
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
        var productId = TryResolveStoreProductId(id);
        string uri;
        if (!string.IsNullOrWhiteSpace(productId))
        {
            uri = $"ms-windows-store://pdp/?ProductId={System.Uri.EscapeDataString(productId)}";
        }
        else if (LooksLikeMsStoreProductId(id))
        {
            uri = $"ms-windows-store://pdp/?ProductId={System.Uri.EscapeDataString(id)}";
        }
        else
        {
            // Last resort: search — can hit a similarly named different app (avoid for Copilot).
            uri = $"ms-windows-store://search/?query={System.Uri.EscapeDataString(string.IsNullOrWhiteSpace(name) ? id : name)}";
        }

        return new SoftwareUpdateExternalAction
        {
            PackageName = name,
            PackageId = id,
            Kind = SoftwareUpdateExternalKind.MicrosoftStore,
            ButtonLabel = "Microsoft Store öffnen",
            Uri = uri,
            Hint = hint ?? BuildStoreHint(id, name)
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

    public static string? TryResolveStoreProductId(string? wingetOrStoreId)
    {
        if (string.IsNullOrWhiteSpace(wingetOrStoreId)) return null;
        if (LooksLikeMsStoreProductId(wingetOrStoreId)) return wingetOrStoreId;
        return KnownStoreProductIds.TryGetValue(wingetOrStoreId.Trim(), out var pid) ? pid : null;
    }

    private static string BuildStoreHint(string id, string name)
    {
        if (id.Contains("365Copilot", StringComparison.OrdinalIgnoreCase)
            || name.Contains("365 Copilot", StringComparison.OrdinalIgnoreCase))
        {
            return "Microsoft 365 Copilot (Store-Id 9WZDNCRD29V9) — nicht die separate App „Microsoft Copilot“.";
        }

        return "Update über den Microsoft Store (Produktseite, keine Suche).";
    }

    private static bool IsKnownWindowsUpdateApp(SoftwareUpdateItem item)
    {
        var id = item.Id ?? "";
        var name = item.Name ?? "";
        if (id.Equals("Microsoft.Edge", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.Contains("Microsoft.Edge", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("Microsoft Edge", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsKnownMicrosoftStoreApp(SoftwareUpdateItem item)
    {
        var id = item.Id ?? "";
        var name = item.Name ?? "";
        if (TryResolveStoreProductId(id) is not null) return true;
        if (name.Contains("365 Copilot", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.Contains("365Copilot", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("Outlook for Windows", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.Contains("OutlookForWindows", StringComparison.OrdinalIgnoreCase)) return true;
        // Avoid matching bare „Copilot“ → consumer Copilot search trap
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
