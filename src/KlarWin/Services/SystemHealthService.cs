namespace KlarWin.Services;

public sealed record HealthCategory(
    string Id,
    string Label,
    HealthLevel Level,
    string Summary,
    double BarPercent);

public sealed record HealthHint(
    HealthLevel Level,
    string Title,
    string Message);

public sealed record SystemHealthReport(
    HealthLevel Overall,
    string OverallSummary,
    IReadOnlyList<HealthCategory> Categories,
    IReadOnlyList<HealthHint> Hints,
    string Disclaimer);

public sealed class SystemHealthTracker
{
    private readonly Dictionary<string, (HealthLevel Level, DateTime SinceUtc)> _since = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan WatchDuration = TimeSpan.FromMinutes(3);

    public HealthLevel EffectiveLevel(string key, HealthLevel instant)
    {
        if (instant == HealthLevel.Unknown) return HealthLevel.Unknown;
        if (instant == HealthLevel.Ok)
        {
            _since.Remove(key);
            return HealthLevel.Ok;
        }

        if (instant == HealthLevel.Critical) return HealthLevel.Critical;

        if (!_since.TryGetValue(key, out var entry) || entry.Level != instant)
        {
            _since[key] = (instant, DateTime.UtcNow);
            return HealthLevel.Ok;
        }

        return DateTime.UtcNow - entry.SinceUtc >= WatchDuration ? instant : HealthLevel.Ok;
    }
}

public static class SystemHealthService
{
    private static readonly TimeSpan SmartCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ReliabilityCacheDuration = TimeSpan.FromMinutes(5);
    private static DateTime _smartReadUtc = DateTime.MinValue;
    private static IReadOnlyList<DiskSmartStatus> _smartCache = [];
    private static DateTime _reliabilityReadUtc = DateTime.MinValue;
    private static IReadOnlyList<SystemReliabilityIssue> _reliabilityCache = [];

    public static SystemHealthReport Evaluate(
        LivePerformanceSample live,
        SystemHealthTracker tracker,
        bool isAdministrator)
    {
        var categories = new List<HealthCategory>();
        var hints = new List<HealthHint>();

        EvaluateThermik(live.Temperatures, tracker, categories, hints, isAdministrator);
        EvaluateRam(live.Snapshot.RamPercent, tracker, categories, hints);
        EvaluateDiskSpace(live.Snapshot, tracker, categories, hints);
        EvaluateBattery(tracker, categories, hints);
        EvaluateSmart(tracker, categories, hints);
        EvaluateReliability(tracker, categories, hints);

        var known = categories.Where(c => c.Level != HealthLevel.Unknown).ToList();
        var overall = known.Count == 0
            ? HealthLevel.Unknown
            : ComputeOverall(known);

        var overallSummary = overall switch
        {
            HealthLevel.Ok => "In Ordnung",
            HealthLevel.Watch => $"{hints.Count(h => h.Level >= HealthLevel.Watch)} Hinweis(e)",
            HealthLevel.Critical => "Hinweis vorhanden",
            _ => isAdministrator ? "Teilweise unbekannt" : "Mit Adminrechten genauer"
        };

        if (!isAdministrator && categories.Any(c => c.Id == "thermik" && c.Level == HealthLevel.Unknown))
        {
            hints.Insert(0, new HealthHint(
                HealthLevel.Watch,
                "Mehr Thermik-Daten",
                "Mit „Adminrechte freischalten“ werden oft CPU- und Mainboard-Sensoren sichtbar — die Gesundheitsbewertung wird dann genauer."));
        }

        return new SystemHealthReport(
            overall,
            overallSummary,
            categories,
            hints.Take(6).ToList(),
            "Grobe Einschätzung auf Basis verfügbarer Sensoren — keine sichere Vorhersage, ersetzt keine Fachdiagnose.");
    }

    public static string FormatTileSummary(SystemHealthReport report)
    {
        var label = TemperatureZones.OverallLabel(report.Overall);
        if (report.Hints.Count == 0) return $"Gesundheit: {label}";
        return $"Gesundheit: {label} · {report.Hints.Count} Hinweis(e)";
    }

    private static void EvaluateThermik(
        IReadOnlyList<TemperatureSensor> sensors,
        SystemHealthTracker tracker,
        List<HealthCategory> categories,
        List<HealthHint> hints,
        bool isAdministrator)
    {
        var cpu = PickMaxSensor(sensors, TemperatureZones.IsCpuSensor, preferPackage: true);
        var gpu = PickMaxSensor(sensors, TemperatureZones.IsGpuSensor);
        var board = PickMaxSensor(sensors, TemperatureZones.IsBoardSensor);

        var levels = new List<(string Label, TemperatureSensor? Sensor, bool IsGpu, bool IsBoard)>
        {
            ("CPU", cpu, false, false),
            ("GPU", gpu, true, false),
            ("Mainboard", board, false, true)
        };

        HealthLevel thermikLevel = HealthLevel.Unknown;
        var parts = new List<string>();

        foreach (var (label, sensor, isGpu, isBoard) in levels)
        {
            if (sensor?.Celsius is not double c)
            {
                parts.Add($"{label}: —");
                continue;
            }

            var instant = TemperatureZones.LevelForCelsius(c, isGpu, isBoard);
            var effective = tracker.EffectiveLevel($"thermik-{label}", instant);
            thermikLevel = MaxLevel(thermikLevel, effective);
            parts.Add($"{label}: {c:0} °C ({TemperatureZones.LevelLabel(effective)})");

            if (effective >= HealthLevel.Watch)
            {
                hints.Add(BuildThermalHint(label, sensor.Name, c, effective, isGpu, isBoard));
            }
        }

        if (thermikLevel == HealthLevel.Unknown && sensors.Count > 0)
        {
            thermikLevel = HealthLevel.Ok;
            parts.Add("Keine Gruppensensoren");
        }

        var bar = cpu?.Celsius is double cpuC
            ? TemperatureZones.BarPercentForCelsius(cpuC, false)
            : gpu?.Celsius is double gpuC
                ? TemperatureZones.BarPercentForCelsius(gpuC, true)
                : 0;

        categories.Add(new HealthCategory(
            "thermik",
            "Thermik",
            thermikLevel,
            string.Join(" · ", parts),
            bar));
    }

    private static HealthHint BuildThermalHint(
        string group,
        string sensorName,
        double celsius,
        HealthLevel level,
        bool isGpu,
        bool isBoard)
    {
        if (isBoard && level >= HealthLevel.Watch)
        {
            return new HealthHint(
                level,
                $"{group}: {celsius:0} °C",
                level == HealthLevel.Critical
                    ? "Mainboard/Chipset liegt im roten Bereich — das könnte auf Belüftung oder hohe Umgebungstemperatur hindeuten. Lüfter und Kühlkörper prüfen kann sinnvoll sein."
                    : "Mainboard/Chipset außerhalb des grünen Bereichs — kann auf Belüftung oder Umgebungstemperatur hindeuten. Lüfter und Kühlkörper im Blick behalten.");
        }

        if (group == "CPU")
        {
            return new HealthHint(
                level,
                $"{sensorName}: {celsius:0} °C",
                level == HealthLevel.Critical
                    ? "CPU-Temperatur liegt im roten Bereich — es könnte zu Drosselung kommen. Last reduzieren und Kühlung prüfen kann helfen."
                    : "CPU außerhalb des grünen Bereichs — Lüfter, Untergrund und ggf. Kühlprofil im Blick behalten.");
        }

        return new HealthHint(
            level,
            $"{sensorName}: {celsius:0} °C",
            level == HealthLevel.Critical
                ? "GPU-Temperatur liegt im roten Bereich — Last reduzieren und Belüftung prüfen kann sinnvoll sein."
                : "GPU im gelben Bereich — Belüftung und Auslastung im Blick behalten.");
    }

    private static void EvaluateRam(
        double ramPercent,
        SystemHealthTracker tracker,
        List<HealthCategory> categories,
        List<HealthHint> hints)
    {
        var instant = ramPercent switch
        {
            >= 95 => HealthLevel.Critical,
            >= 85 => HealthLevel.Watch,
            _ => HealthLevel.Ok
        };
        var level = tracker.EffectiveLevel("ram", instant);
        categories.Add(new HealthCategory(
            "ram",
            "Arbeitsspeicher",
            level,
            $"{ramPercent:0} % belegt",
            Math.Clamp(ramPercent, 0, 100)));

        if (level >= HealthLevel.Watch)
        {
            hints.Add(new HealthHint(
                level,
                $"RAM: {ramPercent:0} %",
                level == HealthLevel.Critical
                    ? "Arbeitsspeicher ist sehr hoch belegt — es könnte zu Verlangsamungen kommen. Programme schließen oder Neustart kann helfen."
                    : "Arbeitsspeicher hoch — unnötige Programme schließen kann helfen."));
        }
    }

    private static void EvaluateDiskSpace(
        PerformanceSnapshot snapshot,
        SystemHealthTracker tracker,
        List<HealthCategory> categories,
        List<HealthHint> hints)
    {
        var usedPercent = snapshot.DiskTotalBytes > 0
            ? 100.0 - snapshot.DiskFreePercent
            : 0;
        var instant = usedPercent switch
        {
            >= 95 => HealthLevel.Critical,
            >= 85 => HealthLevel.Watch,
            _ => HealthLevel.Ok
        };
        var level = tracker.EffectiveLevel("disk-space", instant);
        categories.Add(new HealthCategory(
            "disk-space",
            "Festplatte (Belegung)",
            level,
            $"{snapshot.DiskName} · {usedPercent:0} % belegt",
            Math.Clamp(usedPercent, 0, 100)));

        if (level >= HealthLevel.Watch)
        {
            hints.Add(new HealthHint(
                level,
                $"{snapshot.DiskName}: {usedPercent:0} % voll",
                level == HealthLevel.Critical
                    ? "Systemlaufwerk ist fast voll — Updates und Programme könnten scheitern. Speicher freigeben kann sinnvoll sein."
                    : "Systemlaufwerk wird voll — rechtzeitig aufräumen kann helfen."));
        }
    }

    private static void EvaluateBattery(
        SystemHealthTracker tracker,
        List<HealthCategory> categories,
        List<HealthHint> hints)
    {
        if (!LaptopPowerService.HasBattery())
        {
            return;
        }

        var battery = BatteryHealthService.Read();
        BatteryHistoryService.Record(battery);
        var level = battery.WearPercent is int wear
            ? tracker.EffectiveLevel("battery", battery.Level)
            : HealthLevel.Unknown;

        categories.Add(new HealthCategory(
            "battery",
            "Akku",
            level,
            battery.Summary,
            battery.WearPercent ?? 0));

        var trend = BatteryHistoryService.FormatTrendHint(battery.WearPercent);
        if (!string.IsNullOrWhiteSpace(trend))
        {
            hints.Add(new HealthHint(HealthLevel.Ok, "Akku-Verlauf", trend));
        }

        if (level == HealthLevel.Watch)
        {
            hints.Add(new HealthHint(
                level,
                $"Akku: {battery.WearPercent} % Restkapazität",
                battery.CycleCount is > 0
                    ? $"Die gemessene Restkapazität ist etwas niedrig ({battery.CycleCount} Ladezyklen gemeldet) — unterwegs könnte die Laufzeit kürzer sein als früher."
                    : "Die gemessene Restkapazität ist etwas niedrig — unterwegs könnte die Laufzeit kürzer sein als früher."));
        }
        else if (level == HealthLevel.Critical)
        {
            hints.Add(new HealthHint(
                level,
                $"Akku: {battery.WearPercent} % Restkapazität",
                battery.CycleCount is > 0
                    ? $"Die gemessene Restkapazität ist deutlich niedrig ({battery.CycleCount} Ladezyklen gemeldet) — es könnte sein, dass dieser Akku nur noch eine kürzere Laufzeit hat. Eventuell lohnt sich ein Check, besonders bei mobiler Nutzung."
                    : "Die gemessene Restkapazität ist deutlich niedrig — es könnte sein, dass dieser Akku nur noch eine kürzere Laufzeit hat. Eventuell lohnt sich ein Check, besonders bei mobiler Nutzung."));
        }
        else if (battery.WearPercent is null && battery.ChargePercent is not null)
        {
            hints.Add(new HealthHint(
                HealthLevel.Watch,
                "Akku: Restkapazität unbekannt",
                "Windows meldet keinen Verschleiß-Wert — Kapazitätstest im Hersteller-Tool (z. B. Dell Power Manager, Lenovo Vantage) oder Batteriebericht: powercfg /batteryreport"));
        }
    }

    private static void EvaluateSmart(
        SystemHealthTracker tracker,
        List<HealthCategory> categories,
        List<HealthHint> hints)
    {
        var smart = ReadSmartCached();
        if (smart.Count == 0)
        {
            categories.Add(new HealthCategory(
                "smart",
                "Festplatte (SMART)",
                HealthLevel.Unknown,
                "SMART nicht verfügbar",
                0));
            return;
        }

        var worst = smart.Max(s => s.Level);
        var level = tracker.EffectiveLevel("smart", worst);
        var failing = smart.Where(s => s.PredictFailure || s.Level >= HealthLevel.Watch).ToList();
        var summary = failing.Count == 0
            ? $"{smart.Count} Laufwerk(e) · SMART ok"
            : string.Join(" · ", failing.Take(2).Select(f => $"{f.Device}: {f.Detail}"));

        categories.Add(new HealthCategory(
            "smart",
            "Festplatte (SMART)",
            level,
            summary,
            level == HealthLevel.Ok ? 15 : 85));

        foreach (var disk in smart.Where(s => s.PredictFailure))
        {
            hints.Add(new HealthHint(
                HealthLevel.Critical,
                $"SMART: {disk.Device}",
                "SMART meldet ein mögliches Risiko — ein Backup zu prüfen kann sinnvoll sein."));
        }

        foreach (var disk in smart.Where(s => !s.PredictFailure && s.Level >= HealthLevel.Watch))
        {
            hints.Add(new HealthHint(
                HealthLevel.Watch,
                $"Laufwerk: {disk.Device}",
                $"{disk.Detail} — genauer prüfen und ein Backup in Betracht ziehen kann sinnvoll sein."));
        }
    }

    private static void EvaluateReliability(
        SystemHealthTracker tracker,
        List<HealthCategory> categories,
        List<HealthHint> hints)
    {
        var issues = ReadReliabilityCached();
        var instant = SystemReliabilityService.LevelForIssues(issues);
        var level = tracker.EffectiveLevel("reliability", instant);
        var summary = issues.Count == 0
            ? "Keine relevanten Systemfehler (7 Tage)"
            : $"{issues.Count} Fehler in 7 Tagen";

        categories.Add(new HealthCategory(
            "reliability",
            "Systemprotokoll",
            level,
            summary,
            issues.Count == 0 ? 10 : Math.Min(100, 20 + issues.Count * 15)));

        foreach (var issue in issues.Take(2))
        {
            hints.Add(new HealthHint(
                level,
                $"{issue.Source} ({issue.When:dd.MM.})",
                issue.Message));
        }
    }

    private static IReadOnlyList<DiskSmartStatus> ReadSmartCached()
    {
        if (DateTime.UtcNow - _smartReadUtc < SmartCacheDuration) return _smartCache;
        _smartCache = DiskSmartHealthService.ReadAll();
        _smartReadUtc = DateTime.UtcNow;
        return _smartCache;
    }

    private static IReadOnlyList<SystemReliabilityIssue> ReadReliabilityCached()
    {
        if (DateTime.UtcNow - _reliabilityReadUtc < ReliabilityCacheDuration) return _reliabilityCache;
        _reliabilityCache = SystemReliabilityService.ReadRecentIssues();
        _reliabilityReadUtc = DateTime.UtcNow;
        return _reliabilityCache;
    }

    private static TemperatureSensor? PickMaxSensor(
        IReadOnlyList<TemperatureSensor> sensors,
        Func<string, bool> predicate,
        bool preferPackage = false)
    {
        var matches = sensors
            .Where(s => s.Celsius is double && predicate(s.Name))
            .ToList();
        if (matches.Count == 0) return null;

        if (preferPackage)
        {
            var package = matches.FirstOrDefault(s =>
                s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase));
            if (package is not null) return package;

            var coreMax = matches.FirstOrDefault(s =>
                s.Name.Contains("Core Max", StringComparison.OrdinalIgnoreCase));
            if (coreMax is not null) return coreMax;
        }

        return matches.OrderByDescending(s => s.Celsius).First();
    }

    private static HealthLevel ComputeOverall(IReadOnlyList<HealthCategory> known)
    {
        var max = known.Max(c => c.Level);
        if (max != HealthLevel.Critical)
        {
            return max;
        }

        var criticalCount = known.Count(c => c.Level == HealthLevel.Critical);
        if (criticalCount == 1 && known.Count(c => c.Level == HealthLevel.Ok) >= known.Count - 1)
        {
            return HealthLevel.Watch;
        }

        return max;
    }

    private static HealthLevel MaxLevel(HealthLevel a, HealthLevel b)
    {
        if (a == HealthLevel.Unknown) return b;
        if (b == HealthLevel.Unknown) return a;
        return (HealthLevel)Math.Max((int)a, (int)b);
    }
}
