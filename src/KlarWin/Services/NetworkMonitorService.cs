using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace KlarWin.Services;

public sealed class NetworkSnapshot
{
    public string AdapterName { get; init; } = "—";
    public long AdapterDownBytesPerSec { get; init; }
    public long AdapterUpBytesPerSec { get; init; }
    public long AdapterSpeedBits { get; init; }
    public string Gateway { get; init; } = "";
    public int? PingMs { get; init; }
    public bool RouterReachable { get; init; }
    public string RouterName { get; init; } = "";
    public string WanStatus { get; init; } = "";
    public string WanType { get; init; } = "";
    public string ExternalIp { get; init; } = "";
    public TimeSpan WanUptime { get; init; }
    public long WanDownBytesPerSec { get; init; }
    public long WanUpBytesPerSec { get; init; }
    public long WanDownBitsMax { get; init; }
    public long WanUpBitsMax { get; init; }
    public long WanBytesReceived { get; init; }
    public long WanBytesSent { get; init; }
    public int HostCount { get; init; }
    public int ActiveHostCount { get; init; }
    public IReadOnlyList<RouterHost> Hosts { get; init; } = [];
    public string Note { get; init; } = "";
}

public sealed class RouterHost
{
    public string Name { get; init; } = "";
    public string Ip { get; init; } = "";
    public string Mac { get; init; } = "";
    public string InterfaceType { get; init; } = "";
    public bool Active { get; init; }
}

public sealed class NetworkMonitorService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<string, long> _prevRecv = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _prevSent = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _prevSampleUtc = DateTime.MinValue;
    private NetworkSnapshot _last = new();
    private IReadOnlyList<RouterHost> _cachedHosts = [];
    private DateTime _hostsCachedUtc = DateTime.MinValue;
    private string? _gateway;
    private string? _routerBase;
    private string _wanCommonPath = "/igdupnp/control/WANCommonIFC1";
    private string _wanIpPath = "/igdupnp/control/WANIPConn1";
    private string _hostsPath = "/upnp/control/hosts";
    private bool _hostsSupported;
    private bool _disposed;

    public NetworkSnapshot Last => _last;

    public NetworkSnapshot ReadLocal()
    {
        var gateway = FindGateway();
        _gateway = gateway;
        var nic = BestAdapter();
        var elapsed = DateTime.UtcNow - _prevSampleUtc;
        long down = 0, up = 0, speed = 0;
        var name = "—";

        if (nic is not null)
        {
            name = nic.Name;
            speed = nic.Speed > 0 && nic.Speed < long.MaxValue / 8 ? nic.Speed : 0;
            try
            {
                var stats = nic.GetIPStatistics();
                if (_prevSampleUtc != DateTime.MinValue && elapsed.TotalSeconds > 0.2)
                {
                    var seconds = elapsed.TotalSeconds;
                    if (_prevRecv.TryGetValue(nic.Id, out var prevR))
                    {
                        down = (long)Math.Max(0, (stats.BytesReceived - prevR) / seconds);
                    }
                    if (_prevSent.TryGetValue(nic.Id, out var prevS))
                    {
                        up = (long)Math.Max(0, (stats.BytesSent - prevS) / seconds);
                    }
                }

                _prevRecv[nic.Id] = stats.BytesReceived;
                _prevSent[nic.Id] = stats.BytesSent;
            }
            catch
            {
                // adapter vanished
            }
        }

        _prevSampleUtc = DateTime.UtcNow;
        _last = new NetworkSnapshot
        {
            AdapterName = name,
            AdapterDownBytesPerSec = down,
            AdapterUpBytesPerSec = up,
            AdapterSpeedBits = speed,
            Gateway = gateway,
            PingMs = _last.PingMs,
            RouterReachable = _last.RouterReachable,
            RouterName = _last.RouterName,
            WanStatus = _last.WanStatus,
            WanType = _last.WanType,
            ExternalIp = _last.ExternalIp,
            WanUptime = _last.WanUptime,
            WanDownBytesPerSec = _last.WanDownBytesPerSec,
            WanUpBytesPerSec = _last.WanUpBytesPerSec,
            WanDownBitsMax = _last.WanDownBitsMax,
            WanUpBitsMax = _last.WanUpBitsMax,
            WanBytesReceived = _last.WanBytesReceived,
            WanBytesSent = _last.WanBytesSent,
            HostCount = _last.HostCount,
            ActiveHostCount = _last.ActiveHostCount,
            Hosts = _cachedHosts,
            Note = _last.Note
        };
        return _last;
    }

    public async Task<int?> PingGatewayAsync()
    {
        var gateway = _gateway ?? FindGateway();
        if (string.IsNullOrWhiteSpace(gateway)) return null;
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(gateway, 800);
            var ms = reply.Status == IPStatus.Success ? (int?)reply.RoundtripTime : null;
            _last = Clone(_last, pingMs: ms);
            return ms;
        }
        catch
        {
            _last = Clone(_last, pingMs: null);
            return null;
        }
    }

    public async Task<NetworkSnapshot> RefreshRouterAsync(bool includeHosts)
    {
        var gateway = _gateway ?? FindGateway();
        if (string.IsNullOrWhiteSpace(gateway))
        {
            _last = Clone(_last, note: "Kein Router / Gateway gefunden.");
            return _last;
        }

        if (_routerBase is null)
        {
            await DiscoverIgdAsync(gateway);
        }

        if (_routerBase is null)
        {
            var neighbors = ReadLanNeighbors();
            _cachedHosts = neighbors;
            _last = Clone(_last,
                note: $"Gateway {gateway}: kein UPnP/IGD. PC-Netzwerk und {neighbors.Count} LAN-Nachbarn bleiben sichtbar.",
                hosts: neighbors,
                hostCount: neighbors.Count,
                activeHostCount: neighbors.Count(h => h.Active));
            return _last;
        }

        try
        {
            const string wanCommon = "urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1";
            const string wanIp = "urn:schemas-upnp-org:service:WANIPConnection:1";

            var addon = await SoapAsync(_wanCommonPath, wanCommon, "GetAddonInfos");
            var totalsRecv = string.IsNullOrEmpty(addon)
                ? await SoapAsync(_wanCommonPath, wanCommon, "GetTotalBytesReceived")
                : addon;
            var totalsSent = string.IsNullOrEmpty(addon)
                ? await SoapAsync(_wanCommonPath, wanCommon, "GetTotalBytesSent")
                : addon;
            var link = string.IsNullOrEmpty(addon)
                ? await SoapAsync(_wanCommonPath, wanCommon, "GetCommonLinkProperties")
                : addon;
            var status = await SoapAsync(_wanIpPath, wanIp, "GetStatusInfo");
            if (string.IsNullOrEmpty(status))
            {
                status = await SoapAsync(_wanIpPath, "urn:schemas-upnp-org:service:WANPPPConnection:1", "GetStatusInfo");
            }

            var ipXml = await SoapAsync(_wanIpPath, wanIp, "GetExternalIPAddress");
            if (string.IsNullOrEmpty(ipXml))
            {
                ipXml = await SoapAsync(_wanIpPath, "urn:schemas-upnp-org:service:WANPPPConnection:1", "GetExternalIPAddress");
            }

            var nameXml = await GetStringAsync("/igddesc.xml");
            var source = !string.IsNullOrEmpty(addon) ? addon : (link + totalsRecv + totalsSent);

            var downMax = ParseLong(source, "NewX_AVM_DE_Layer1DownstreamMaxBitRate64")
                          ?? ParseLong(source, "NewLayer1DownstreamMaxBitRate")
                          ?? 0;
            var upMax = ParseLong(source, "NewX_AVM_DE_Layer1UpstreamMaxBitRate64")
                        ?? ParseLong(source, "NewLayer1UpstreamMaxBitRate")
                        ?? 0;
            var received = ParseLong(source, "NewX_AVM_DE_TotalBytesReceived64")
                           ?? ParseLong(source, "NewTotalBytesReceived")
                           ?? 0;
            var sent = ParseLong(source, "NewX_AVM_DE_TotalBytesSent64")
                       ?? ParseLong(source, "NewTotalBytesSent")
                       ?? 0;

            var hosts = _cachedHosts;
            var hostCount = _last.HostCount;
            if (includeHosts || hosts.Count == 0 || (DateTime.UtcNow - _hostsCachedUtc).TotalSeconds > 25)
            {
                hosts = await ReadHostsAsync();
                if (hosts.Count == 0)
                {
                    hosts = ReadLanNeighbors();
                }

                _cachedHosts = hosts;
                _hostsCachedUtc = DateTime.UtcNow;
                hostCount = hosts.Count;
            }
            else if (hostCount == 0)
            {
                hostCount = hosts.Count;
            }

            var uptime = ParseLong(status, "NewUptime") ?? 0;
            var friendly = ExtractXml(nameXml, "friendlyName")
                           ?? ExtractXml(nameXml, "modelName")
                           ?? "Router";

            _last = new NetworkSnapshot
            {
                AdapterName = _last.AdapterName,
                AdapterDownBytesPerSec = _last.AdapterDownBytesPerSec,
                AdapterUpBytesPerSec = _last.AdapterUpBytesPerSec,
                AdapterSpeedBits = _last.AdapterSpeedBits,
                Gateway = gateway,
                PingMs = _last.PingMs,
                RouterReachable = true,
                RouterName = friendly,
                WanStatus = ExtractXml(status, "NewConnectionStatus") ?? "",
                WanType = ExtractXml(source, "NewX_AVM_DE_WANAccessType")
                          ?? ExtractXml(source, "NewWANAccessType")
                          ?? "",
                ExternalIp = ExtractXml(ipXml, "NewExternalIPAddress") ?? "",
                WanUptime = TimeSpan.FromSeconds(uptime),
                WanDownBytesPerSec = ParseLong(source, "NewByteReceiveRate") ?? 0,
                WanUpBytesPerSec = ParseLong(source, "NewByteSendRate") ?? 0,
                WanDownBitsMax = downMax,
                WanUpBitsMax = upMax,
                WanBytesReceived = received,
                WanBytesSent = sent,
                HostCount = hostCount,
                ActiveHostCount = hosts.Count(h => h.Active),
                Hosts = hosts,
                Note = _hostsSupported ? "" : "Geräteliste über LAN-Nachbarn (Router liefert keine Host-Liste ohne Passwort)."
            };
            return _last;
        }
        catch (Exception ex)
        {
            _last = Clone(_last, note: "Router: " + ex.Message);
            return _last;
        }
    }

    public void OpenRouterPage()
    {
        var target = _gateway ?? FindGateway();
        if (string.IsNullOrWhiteSpace(target)) target = "192.168.1.1";
        Process.Start(new ProcessStartInfo
        {
            FileName = $"http://{target}/",
            UseShellExecute = true
        });
    }

    public static string FormatRate(long bytesPerSecond)
    {
        var bits = bytesPerSecond * 8.0;
        if (bits < 1000) return $"{bits:0} bit/s";
        if (bits < 1_000_000) return $"{bits / 1000:0} kbit/s";
        return $"{bits / 1_000_000:0.0} Mbit/s";
    }

    public static string FormatBits(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0) return "—";
        if (bitsPerSecond < 1_000_000) return $"{bitsPerSecond / 1000.0:0} kbit/s";
        return $"{bitsPerSecond / 1_000_000.0:0.0} Mbit/s";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }

    private async Task DiscoverIgdAsync(string gateway)
    {
        var hosts = new List<string> { gateway, "fritz.box", "router.asus.com", "myrouter", "tplinkwifi.net", "speedport.ip" };
        foreach (var host in hosts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var port in new[] { 49000, 49152, 5000 })
            {
                try
                {
                    var baseUrl = $"http://{host}:{port}";
                    using var response = await _http.GetAsync(baseUrl + "/igddesc.xml");
                    if (!response.IsSuccessStatusCode) continue;
                    var xml = await response.Content.ReadAsStringAsync();
                    _routerBase = baseUrl;
                    ParseControlUrls(xml);
                    await ProbeHostsSupportAsync();
                    return;
                }
                catch
                {
                    // try next
                }
            }
        }
    }

    private void ParseControlUrls(string xml)
    {
        var wanCommon = FindControlUrl(xml, "WANCommonInterfaceConfig");
        var wanIp = FindControlUrl(xml, "WANIPConnection")
                    ?? FindControlUrl(xml, "WANPPPConnection");
        if (!string.IsNullOrWhiteSpace(wanCommon)) _wanCommonPath = wanCommon;
        if (!string.IsNullOrWhiteSpace(wanIp)) _wanIpPath = wanIp;
    }

    private static string? FindControlUrl(string xml, string serviceHint)
    {
        var pattern = $@"<serviceType>[^<]*{serviceHint}[^<]*</serviceType>\s*<serviceId>[^<]*</serviceId>\s*<controlURL>([^<]+)</controlURL>";
        var match = Regex.Match(xml, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success) return null;
        var url = match.Groups[1].Value.Trim();
        return url.StartsWith('/') ? url : "/" + url;
    }

    private async Task ProbeHostsSupportAsync()
    {
        var xml = await SoapAsync(_hostsPath, "urn:dslforum-org:service:Hosts:1", "GetHostNumberOfEntries");
        _hostsSupported = !string.IsNullOrEmpty(xml) && ParseLong(xml, "NewHostNumberOfEntries") is not null;
    }

    private async Task<IReadOnlyList<RouterHost>> ReadHostsAsync()
    {
        if (!_hostsSupported) return [];

        var countXml = await SoapAsync(_hostsPath, "urn:dslforum-org:service:Hosts:1", "GetHostNumberOfEntries");
        var count = (int)(ParseLong(countXml, "NewHostNumberOfEntries") ?? 0);
        if (count <= 0) return [];

        count = Math.Min(count, 80);
        var bag = new RouterHost[count];
        await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = 6 }, async (index, token) =>
        {
            string xml;
            try
            {
                var inner = $"<NewIndex>{index}</NewIndex>";
                xml = await SoapAsync(_hostsPath, "urn:dslforum-org:service:Hosts:1", "GetGenericHostEntry", inner, token);
            }
            catch
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(xml)) return;
            bag[index] = new RouterHost
            {
                Name = ExtractXml(xml, "NewHostName") ?? "—",
                Ip = ExtractXml(xml, "NewIPAddress") ?? "",
                Mac = ExtractXml(xml, "NewMACAddress") ?? "",
                InterfaceType = ExtractXml(xml, "NewInterfaceType") switch
                {
                    "802.11" => "WLAN",
                    "Ethernet" => "LAN",
                    var other => string.IsNullOrWhiteSpace(other) ? "—" : other
                },
                Active = ExtractXml(xml, "NewActive") == "1"
            };
        });

        return bag
            .Where(h => h is not null && (!string.IsNullOrWhiteSpace(h.Ip) || h.Name is not "—"))
            .OrderByDescending(h => h.Active)
            .ThenBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<RouterHost> ReadLanNeighbors()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString()))
                .Distinct()
                .Take(1)
                .SelectMany(_ =>
                {
                    using var ping = new Ping();
                    return IPGlobalProperties.GetIPGlobalProperties()
                        .GetActiveTcpConnections()
                        .Select(c => c.RemoteEndPoint.Address)
                        .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                        .Select(a => a.ToString())
                        .Distinct()
                        .Take(30)
                        .Select(ip => new RouterHost
                        {
                            Name = ip,
                            Ip = ip,
                            InterfaceType = "LAN",
                            Active = true
                        });
                })
                .GroupBy(h => h.Ip)
                .Select(g => g.First())
                .OrderBy(h => h.Ip)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private async Task<string> SoapAsync(string path, string urn, string action, string inner = "", CancellationToken cancellationToken = default)
    {
        if (_routerBase is null || string.IsNullOrWhiteSpace(path)) return "";
        var body =
            "<?xml version=\"1.0\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
            "<s:Body>" +
            $"<u:{action} xmlns:u=\"{urn}\">{inner}</u:{action}>" +
            "</s:Body></s:Envelope>";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _routerBase + path);
            request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{urn}#{action}\"");
            request.Content = new StringContent(body, Encoding.UTF8, "text/xml");
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return "";
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            return "";
        }
    }

    private async Task<string> GetStringAsync(string path)
    {
        if (_routerBase is null) return "";
        try
        {
            return await _http.GetStringAsync(_routerBase + path);
        }
        catch
        {
            return "";
        }
    }

    private static string FindGateway()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;
            foreach (var gateway in nic.GetIPProperties().GatewayAddresses)
            {
                if (gateway.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    return gateway.Address.ToString();
                }
            }
        }

        return "";
    }

    private static NetworkInterface? BestAdapter()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => n.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .Where(n => n.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
            .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Count)
            .ThenByDescending(n => n.Speed)
            .FirstOrDefault();
    }

    private static NetworkSnapshot Clone(
        NetworkSnapshot source,
        int? pingMs = null,
        string? note = null,
        IReadOnlyList<RouterHost>? hosts = null,
        int? hostCount = null,
        int? activeHostCount = null) => new()
    {
        AdapterName = source.AdapterName,
        AdapterDownBytesPerSec = source.AdapterDownBytesPerSec,
        AdapterUpBytesPerSec = source.AdapterUpBytesPerSec,
        AdapterSpeedBits = source.AdapterSpeedBits,
        Gateway = source.Gateway,
        PingMs = pingMs ?? source.PingMs,
        RouterReachable = source.RouterReachable,
        RouterName = source.RouterName,
        WanStatus = source.WanStatus,
        WanType = source.WanType,
        ExternalIp = source.ExternalIp,
        WanUptime = source.WanUptime,
        WanDownBytesPerSec = source.WanDownBytesPerSec,
        WanUpBytesPerSec = source.WanUpBytesPerSec,
        WanDownBitsMax = source.WanDownBitsMax,
        WanUpBitsMax = source.WanUpBitsMax,
        WanBytesReceived = source.WanBytesReceived,
        WanBytesSent = source.WanBytesSent,
        HostCount = hostCount ?? source.HostCount,
        ActiveHostCount = activeHostCount ?? source.ActiveHostCount,
        Hosts = hosts ?? source.Hosts,
        Note = note ?? source.Note
    };

    private static string? ExtractXml(string? xml, string tag)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        var match = Regex.Match(xml, $"<{tag}>(.*?)</{tag}>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : null;
    }

    private static long? ParseLong(string? xml, string tag)
    {
        var text = ExtractXml(xml, tag);
        if (string.IsNullOrWhiteSpace(text)) return null;
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
