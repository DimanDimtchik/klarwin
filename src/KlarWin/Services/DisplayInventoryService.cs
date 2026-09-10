using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace KlarWin.Services;

public static class DisplayInventoryService
{
    public static IReadOnlyList<MonitorInfo> ReadMonitors()
    {
        var edid = ReadEdidMonitors();
        var screens = EnumScreens();
        var list = new List<MonitorInfo>();
        var usedEdid = new HashSet<int>();

        foreach (var screen in screens
                     .OrderByDescending(s => s.IsPrimary)
                     .ThenBy(s => s.X)
                     .ThenBy(s => s.Y))
        {
            var matchIndex = FindBestEdid(edid, screen, usedEdid);
            EdidMonitor? ed = matchIndex >= 0 ? edid[matchIndex] : null;
            if (matchIndex >= 0) usedEdid.Add(matchIndex);

            var name = !string.IsNullOrWhiteSpace(ed?.Name)
                ? ed!.Name
                : (screen.IsPrimary ? "Hauptanzeige" : "Monitor");
            var manufacturer = ed?.Manufacturer ?? "";
            var serial = ed?.Serial ?? "";
            var product = ed?.ProductCode ?? "";
            var id = BuildId(manufacturer, product, serial, name, screen.Width, screen.Height);

            list.Add(new MonitorInfo
            {
                Id = id,
                Name = name,
                Manufacturer = manufacturer,
                Serial = serial,
                Width = screen.Width,
                Height = screen.Height,
                X = screen.X,
                Y = screen.Y,
                IsPrimary = screen.IsPrimary,
                DeviceName = screen.DeviceName
            });
        }

        return list;
    }

    public static string BuildFingerprint(IEnumerable<MonitorInfo> monitors) =>
        string.Join("|", monitors.Select(m => m.Id).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

    private static string BuildId(string manufacturer, string product, string serial, string name, int w, int h)
    {
        if (!string.IsNullOrWhiteSpace(manufacturer) || !string.IsNullOrWhiteSpace(product))
        {
            var core = $"{manufacturer}:{product}";
            if (!string.IsNullOrWhiteSpace(serial) && serial != "0")
                core += $":{serial}";
            if (!string.IsNullOrWhiteSpace(name) && name is not ("Hauptanzeige" or "Monitor"))
                core += $":{name}";
            return core;
        }

        return $"geom:{w}x{h}:{name}";
    }

    private static int FindBestEdid(IReadOnlyList<EdidMonitor> edid, ScreenRect screen, HashSet<int> used)
    {
        for (var i = 0; i < edid.Count; i++)
        {
            if (used.Contains(i)) continue;
            if (screen.IsPrimary && edid[i].DiagonalCm < 50) return i;
            if (!screen.IsPrimary && edid[i].DiagonalCm >= 50) return i;
        }

        for (var i = 0; i < edid.Count; i++)
        {
            if (!used.Contains(i)) return i;
        }

        return -1;
    }

    private static List<ScreenRect> EnumScreens()
    {
        var list = new List<ScreenRect>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            var info = new MonitorInfoEx();
            info.cbSize = Marshal.SizeOf<MonitorInfoEx>();
            if (GetMonitorInfo(hMonitor, ref info))
            {
                list.Add(new ScreenRect(
                    info.rcMonitor.Left,
                    info.rcMonitor.Top,
                    info.rcMonitor.Right - info.rcMonitor.Left,
                    info.rcMonitor.Bottom - info.rcMonitor.Top,
                    (info.dwFlags & 1) != 0,
                    info.szDevice ?? ""));
            }

            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static IReadOnlyList<EdidMonitor> ReadEdidMonitors()
    {
        var list = new List<EdidMonitor>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorID");
            using var sizeSearcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBasicDisplayParams");
            var sizes = new Dictionary<string, (int W, int H)>(StringComparer.OrdinalIgnoreCase);
            foreach (ManagementObject mo in sizeSearcher.Get())
            {
                var inst = mo["InstanceName"]?.ToString() ?? "";
                var w = Convert.ToInt32(mo["MaxHorizontalImageSize"] ?? 0);
                var h = Convert.ToInt32(mo["MaxVerticalImageSize"] ?? 0);
                sizes[inst] = (w, h);
            }

            foreach (ManagementObject mo in searcher.Get())
            {
                var inst = mo["InstanceName"]?.ToString() ?? "";
                var name = DecodeUshortArray(mo["UserFriendlyName"] as ushort[]);
                var man = DecodeUshortArray(mo["ManufacturerName"] as ushort[]);
                var serial = DecodeUshortArray(mo["SerialNumberID"] as ushort[]);
                var product = DecodeUshortArray(mo["ProductCodeID"] as ushort[]);
                sizes.TryGetValue(inst, out var sz);
                var diag = Math.Sqrt(sz.W * sz.W + sz.H * sz.H);
                list.Add(new EdidMonitor(name, man, serial, product, diag));
            }
        }
        catch
        {
            // WMI optional
        }

        return list;
    }

    private static string DecodeUshortArray(ushort[]? data)
    {
        if (data is null || data.Length == 0) return "";
        var sb = new StringBuilder();
        foreach (var u in data)
        {
            if (u == 0) break;
            sb.Append((char)u);
        }

        return sb.ToString().Trim();
    }

    private sealed record EdidMonitor(string Name, string Manufacturer, string Serial, string ProductCode, double DiagonalCm);
    private sealed record ScreenRect(int X, int Y, int Width, int Height, bool IsPrimary, string DeviceName);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}

public static class WorkplaceNetworkContext
{
    public static (string Gateway, string AdapterName, string WifiSsid) Read()
    {
        string gateway = "";
        string adapter = "";
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                var gw = props.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (gw is null) continue;
                gateway = gw.ToString();
                adapter = nic.Name;
                break;
            }
        }
        catch
        {
            // ignore
        }

        return (gateway, adapter, TryReadWifiSsid());
    }

    private static string TryReadWifiSsid()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "wlan show interfaces",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return "";
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            foreach (var line in output.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("SSID", StringComparison.OrdinalIgnoreCase)
                    && !t.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
                {
                    var idx = t.IndexOf(':');
                    if (idx >= 0) return t[(idx + 1)..].Trim();
                }
            }
        }
        catch
        {
            // ignore
        }

        return "";
    }
}

public static class WorkplaceContextService
{
    public static WorkplaceContext Capture()
    {
        var (gateway, adapter, ssid) = WorkplaceNetworkContext.Read();
        return new WorkplaceContext
        {
            Monitors = DisplayInventoryService.ReadMonitors(),
            Gateway = gateway,
            AdapterName = adapter,
            WifiSsid = ssid
        };
    }
}
