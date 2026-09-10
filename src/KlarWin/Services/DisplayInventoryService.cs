using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace KlarWin.Services;

public static class DisplayInventoryService
{
    private static readonly Dictionary<string, string> ManufacturerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SAM"] = "Samsung",
        ["SNY"] = "Sony",
        ["DEL"] = "Dell",
        ["BNQ"] = "BenQ",
        ["AOC"] = "AOC",
        ["ACI"] = "Asus",
        ["AUO"] = "AU Optronics",
        ["BOE"] = "BOE (Laptop-Panel)",
        ["CMN"] = "Chimei Innolux",
        ["LGD"] = "LG Display",
        ["SDC"] = "Samsung Display",
        ["SHP"] = "Sharp",
        ["HPN"] = "HP",
        ["HWP"] = "HP",
        ["LEN"] = "Lenovo",
        ["VSC"] = "ViewSonic",
        ["GSM"] = "LG",
        ["PHL"] = "Philips",
        ["MSI"] = "MSI",
        ["XEC"] = "MiniFire / XEC",
        ["IVM"] = "Iiyama",
        ["ACR"] = "Acer",
        ["APP"] = "Apple",
        ["TSB"] = "Toshiba",
        ["NCP"] = "NEC"
    };

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
            var hz = ReadRefreshRate(screen.DeviceName);
            var diagIn = ed is { DiagonalCm: > 0 }
                ? Math.Round(ed.DiagonalCm / 2.54, 1)
                : 0;

            list.Add(new MonitorInfo
            {
                Id = id,
                Name = name,
                Manufacturer = manufacturer,
                ManufacturerDisplay = FriendlyManufacturer(manufacturer),
                ProductCode = product,
                Serial = serial,
                Width = screen.Width,
                Height = screen.Height,
                X = screen.X,
                Y = screen.Y,
                IsPrimary = screen.IsPrimary,
                DeviceName = screen.DeviceName,
                DiagonalInches = diagIn,
                RefreshRateHz = hz
            });
        }

        // Position labels left→right for distinction
        var ordered = list.OrderBy(m => m.X).ThenBy(m => m.Y).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var label = ordered.Count switch
            {
                1 => "Einzig",
                2 => i == 0 ? "Links" : "Rechts",
                _ when i == 0 => "Links",
                _ when i == ordered.Count - 1 => "Rechts",
                _ => $"Mitte {i}"
            };
            if (ordered[i].IsPrimary)
                label = string.IsNullOrEmpty(label) ? "Haupt" : $"{label} · Haupt";

            // replace with labeled copy
            var m = ordered[i];
            ordered[i] = new MonitorInfo
            {
                Id = m.Id,
                Name = m.Name,
                Manufacturer = m.Manufacturer,
                ManufacturerDisplay = m.ManufacturerDisplay,
                ProductCode = m.ProductCode,
                Serial = m.Serial,
                Width = m.Width,
                Height = m.Height,
                X = m.X,
                Y = m.Y,
                IsPrimary = m.IsPrimary,
                DeviceName = m.DeviceName,
                DiagonalInches = m.DiagonalInches,
                RefreshRateHz = m.RefreshRateHz,
                PositionLabel = label
            };
        }

        return ordered;
    }

    public static string FriendlyManufacturer(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "Unbekannt";
        return ManufacturerNames.TryGetValue(code.Trim(), out var name) ? name : code.Trim();
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

    private static int ReadRefreshRate(string deviceName)
    {
        try
        {
            var dm = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
            if (EnumDisplaySettings(deviceName, -1, ref dm))
                return dm.dmDisplayFrequency;
        }
        catch
        {
            // optional
        }

        return 0;
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
                list.Add(new EdidMonitor(name, man, serial, product, diag, inst));
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

    private sealed record EdidMonitor(
        string Name, string Manufacturer, string Serial, string ProductCode, double DiagonalCm, string InstanceName);

    private sealed record ScreenRect(int X, int Y, int Width, int Height, bool IsPrimary, string DeviceName);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DevMode lpDevMode);

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
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
