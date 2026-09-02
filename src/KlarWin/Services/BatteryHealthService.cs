using System.Management;

namespace KlarWin.Services;

public sealed record BatteryHealthInfo(
    bool Present,
    /// <summary>Restkapazität in % der Design-Kapazität (100 = wie neu).</summary>
    int? WearPercent,
    int? ChargePercent,
    int? CycleCount,
    HealthLevel Level,
    string Summary);

public static class BatteryHealthService
{
    private const string WmiBatteryNamespace = @"root\WMI";

    public static BatteryHealthInfo Read()
    {
        try
        {
            var snapshot = CollectSnapshot();
            if (!snapshot.Present)
            {
                return new BatteryHealthInfo(
                    false, null, null, null, HealthLevel.Unknown, "Kein Akku erkannt");
            }

            if (snapshot.TryGetCapacityPercent(out var capacityPercent))
            {
                var level = CapacityLevel(capacityPercent);
                var summary = BuildSummary(capacityPercent, snapshot.ChargePercent, snapshot.CycleCount);
                return new BatteryHealthInfo(
                    true, capacityPercent, snapshot.ChargePercent, snapshot.CycleCount, level, summary);
            }

            if (snapshot.ChargePercent is int charge)
            {
                return new BatteryHealthInfo(
                    true,
                    null,
                    charge,
                    snapshot.CycleCount,
                    HealthLevel.Unknown,
                    BuildChargeOnlySummary(charge, snapshot.CycleCount));
            }

            return new BatteryHealthInfo(
                true, null, null, snapshot.CycleCount, HealthLevel.Unknown, "Akku erkannt — Kapazität nicht auslesbar");
        }
        catch
        {
            return new BatteryHealthInfo(false, null, null, null, HealthLevel.Unknown, "Akku: nicht auslesbar");
        }
    }

    private static BatterySnapshot CollectSnapshot()
    {
        var snapshot = new BatterySnapshot();
        ReadWin32Battery(snapshot);
        ReadWmiBatteryData(snapshot);
        snapshot.Present = snapshot.HasAnyBatterySignal();
        return snapshot;
    }

    private static void ReadWin32Battery(BatterySnapshot snapshot)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DesignCapacity, FullChargeCapacity, EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery");
            foreach (var obj in searcher.Get())
            {
                snapshot.ChargePercent ??= ReadChargePercent(obj["EstimatedChargeRemaining"]);
                snapshot.DesignMwh ??= ReadCapacityMwh(obj["DesignCapacity"]);
                snapshot.FullChargeMwh ??= ReadCapacityMwh(obj["FullChargeCapacity"]);
                snapshot.Present = true;
            }
        }
        catch
        {
            // optional
        }
    }

    private static void ReadWmiBatteryData(BatterySnapshot snapshot)
    {
        snapshot.DesignMwh ??= ReadWmiUInt("BatteryStaticData", "DesignedCapacity");
        snapshot.FullChargeMwh ??= ReadWmiUInt("BatteryFullChargedCapacity", "FullChargedCapacity");

        var cycles = ReadWmiUInt("BatteryCycleCount", "CycleCount");
        if (cycles is > 0)
        {
            snapshot.CycleCount ??= (int)Math.Min(cycles.Value, int.MaxValue);
        }

        if (snapshot.ChargePercent is null)
        {
            var remaining = ReadWmiUInt("BatteryStatus", "RemainingCapacity");
            if (remaining is > 0 && snapshot.FullChargeMwh is > 0)
            {
                snapshot.ChargePercent = (int)Math.Clamp(
                    100.0 * remaining.Value / snapshot.FullChargeMwh.Value,
                    0,
                    100);
            }
        }

        if (snapshot.DesignMwh is not null || snapshot.FullChargeMwh is not null || snapshot.ChargePercent is not null)
        {
            snapshot.Present = true;
        }
    }

    private static uint? ReadWmiUInt(string className, string propertyName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(WmiBatteryNamespace, $"SELECT {propertyName} FROM {className}");
            foreach (var obj in searcher.Get())
            {
                var value = ReadCapacityMwh(obj[propertyName]);
                if (value is > 0) return value;
            }
        }
        catch
        {
            // WMI class optional (vendor / permissions)
        }

        return null;
    }

    private static uint? ReadCapacityMwh(object? value)
    {
        var raw = value switch
        {
            uint u => u,
            int i when i > 0 => (uint)i,
            long l when l > 0 => (uint)Math.Min(l, uint.MaxValue),
            ushort us => us,
            short s when s > 0 => (uint)s,
            _ => (uint?)null
        };

        if (raw is null or 0) return null;

        // Win32 liefert auf älteren Systemen manchmal 0.1-Wh-Schritte — beide Werte bleiben vergleichbar.
        return raw;
    }

    private static int? ReadChargePercent(object? value) => value switch
    {
        ushort u when u <= 100 => u,
        uint ui when ui <= 100 => (int)ui,
        int i when i is >= 0 and <= 100 => i,
        byte b => b,
        _ => null
    };

    private static HealthLevel CapacityLevel(int capacityPercent) => capacityPercent switch
    {
        < 60 => HealthLevel.Critical,
        < 80 => HealthLevel.Watch,
        _ => HealthLevel.Ok
    };

    private static string BuildSummary(int capacityPercent, int? chargePercent, int? cycleCount)
    {
        var parts = new List<string> { $"{capacityPercent} % Restkapazität" };
        if (chargePercent is int charge)
        {
            parts.Add($"{charge} % geladen");
        }

        if (cycleCount is > 0)
        {
            parts.Add($"{cycleCount} Ladezyklen");
        }

        return "Akku: " + string.Join(" · ", parts);
    }

    private static string BuildChargeOnlySummary(int chargePercent, int? cycleCount)
    {
        var text = $"Akku: {chargePercent} % geladen — Restkapazität nicht ermittelbar";
        if (cycleCount is > 0)
        {
            text += $" · {cycleCount} Ladezyklen";
        }

        return text;
    }

    private sealed class BatterySnapshot
    {
        public bool Present;
        public uint? DesignMwh;
        public uint? FullChargeMwh;
        public int? ChargePercent;
        public int? CycleCount;

        public bool HasAnyBatterySignal() =>
            Present
            || DesignMwh is > 0
            || FullChargeMwh is > 0
            || ChargePercent is not null;

        public bool TryGetCapacityPercent(out int capacityPercent)
        {
            capacityPercent = 0;
            if (DesignMwh is not > 0 || FullChargeMwh is not > 0) return false;

            capacityPercent = (int)Math.Clamp(100.0 * FullChargeMwh.Value / DesignMwh.Value, 0, 100);
            return true;
        }
    }
}
