using System.Diagnostics;
using System.IO;

namespace KlarWin.Services;

public enum SoftwareUpdateExternalKind
{
    None,
    MicrosoftStore,
    WindowsUpdate,
    /// <summary>Open the app itself (preferably its built-in update / About page).</summary>
    AppSelfUpdate
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
    /// <summary>Optional: launch this exe (e.g. msedge.exe) instead of a protocol URI.</summary>
    public string LaunchFileName { get; init; } = "";
    public string LaunchArguments { get; init; } = "";
    /// <summary>On open: if WU has a matching pending update, open Windows Update instead.</summary>
    public bool ProbeWindowsUpdateFirst { get; init; }
    public string[] WindowsUpdateMatchNeedles { get; init; } = [];

    /// <summary>
    /// winget Id / common aliases → Microsoft Store ProductId (9…), so we open the
    /// correct Store page instead of a fuzzy search (e.g. „Copilot“ ≠ „Microsoft 365 Copilot“).
    /// </summary>
    private static readonly Dictionary<string, string> KnownStoreProductIds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft.365Copilot"] = "9WZDNCRD29V9",
            ["9WZDNCRD29V9"] = "9WZDNCRD29V9",
            ["9NHT9RB2F4HD"] = "9NHT9RB2F4HD",
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

        if (IsEdge(item))
            return EdgeSelfUpdate(item.Name, item.Id);

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
            if (IsEdge(item))
                return EdgeSelfUpdate(item.Name, item.Id);

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

    public static SoftwareUpdateExternalAction EdgeSelfUpdate(string name, string id)
    {
        var edge = FindEdgeExecutable();
        return new SoftwareUpdateExternalAction
        {
            PackageName = name,
            PackageId = id,
            Kind = SoftwareUpdateExternalKind.AppSelfUpdate,
            ButtonLabel = "In Edge aktualisieren",
            Hint = "Prüft kurz Windows Update; wenn dort nichts liegt, öffnet Edge die eigene Update-Seite (Einstellungen → Info).",
            Uri = "microsoft-edge:edge://settings/help",
            LaunchFileName = edge ?? "",
            LaunchArguments = "edge://settings/help",
            ProbeWindowsUpdateFirst = true,
            WindowsUpdateMatchNeedles = ["Microsoft Edge", "Edge"]
        };
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

    private static bool IsEdge(SoftwareUpdateItem item)
    {
        var id = item.Id ?? "";
        var name = item.Name ?? "";
        return id.Equals("Microsoft.Edge", StringComparison.OrdinalIgnoreCase)
               || id.Contains("Microsoft.Edge", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Microsoft Edge", StringComparison.OrdinalIgnoreCase);
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
        return false;
    }

    private static string? FindEdgeExecutable()
    {
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidates =
        [
            Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public bool TryOpen()
    {
        try
        {
            if (ProbeWindowsUpdateFirst
                && WindowsUpdateMatchNeedles.Length > 0
                && WindowsUpdateAvailability.HasPendingMatching(WindowsUpdateMatchNeedles))
            {
                return OpenTarget("ms-settings:windowsupdate", launchFile: null, launchArgs: null);
            }

            return OpenTarget(Uri, LaunchFileName, LaunchArguments);
        }
        catch
        {
            return false;
        }
    }

    private static bool OpenTarget(string? uri, string? launchFile, string? launchArgs)
    {
        if (!string.IsNullOrWhiteSpace(launchFile))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = launchFile,
                Arguments = launchArgs ?? "",
                UseShellExecute = true
            });
            return true;
        }

        if (string.IsNullOrWhiteSpace(uri))
            return false;

        Process.Start(new ProcessStartInfo
        {
            FileName = uri,
            UseShellExecute = true
        });
        return true;
    }
}
