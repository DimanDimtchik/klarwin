using System.Management;

namespace KlarWin.Services;

internal static class LenovoWmiPower
{
    public static bool TryReadSmartFanModeId(out string? modeId)
    {
        modeId = null;
        if (TryInvokeInt("GetSmartFanMode") is not int value)
        {
            return false;
        }

        modeId = value switch
        {
            1 => "dell-thermal-quiet",
            2 => "dell-thermal-optimal",
            3 => "dell-thermal-ultra",
            _ => null
        };
        return modeId is not null;
    }

    public static bool TrySetSmartFanModeId(string optionId)
    {
        var value = optionId switch
        {
            "dell-thermal-quiet" or "dell-thermal-cool" => 1,
            "dell-thermal-optimal" => 2,
            "dell-thermal-ultra" => 3,
            _ => (int?)null
        };
        if (value is null)
        {
            return false;
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM LENOVO_GAMEZONE_DATA");
            foreach (ManagementObject obj in searcher.Get())
            {
                using var inParams = obj.GetMethodParameters("SetSmartFanMode");
                inParams["Data"] = value.Value;
                obj.InvokeMethod("SetSmartFanMode", inParams, null);
                return true;
            }
        }
        catch
        {
            // WMI optional
        }

        return false;
    }

    private static int? TryInvokeInt(string method)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM LENOVO_GAMEZONE_DATA");
            foreach (ManagementObject obj in searcher.Get())
            {
                using var outParams = obj.InvokeMethod(method, null, null);
                if (outParams is null) continue;
                foreach (PropertyData prop in outParams.Properties)
                {
                    if (prop.Value is uint u) return (int)u;
                    if (prop.Value is int i) return i;
                }
            }
        }
        catch
        {
            // WMI optional
        }

        return null;
    }
}
