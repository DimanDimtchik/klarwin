using System.Management;

namespace KlarWin.Services;

/// <summary>
/// Dell AWCC/WMAX thermal profile via root\wmi AWCCWmiMethodFunction (installed OEM WMI).
/// Profile IDs from Linux alienware-wmi-wmax driver.
/// </summary>
internal static class DellAwccWmi
{
    private const uint FailureCode = 0xFFFFFFFF;
    private const byte OpGetCurrentProfile = 0x0B;
    private const byte OpActivateProfile = 0x01;

    public static bool IsAvailable()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM AWCCWmiMethodFunction");
            return searcher.Get().Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryReadThermalModeId(out string? modeId)
    {
        modeId = null;
        if (!TryThermalInformation(OpGetCurrentProfile, 0, out var profileRaw))
        {
            return false;
        }

        modeId = MapProfile(profileRaw);
        return modeId is not null;
    }

    public static bool TryApplyThermalModeId(string optionId)
    {
        foreach (var profile in CandidateProfiles(optionId))
        {
            if (TryThermalControl(OpActivateProfile, profile, out var result) && result != FailureCode)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<byte> CandidateProfiles(string optionId) => optionId switch
    {
        "dell-thermal-quiet" => [0xA3, 0x96],
        "dell-thermal-optimal" => [0xA0, 0x97],
        "dell-thermal-cool" => [0xA2],
        "dell-thermal-ultra" => [0xA4, 0x99, 0xAB, 0xA1],
        _ => []
    };

    private static string? MapProfile(uint raw)
    {
        if (raw == FailureCode)
        {
            return null;
        }

        var profile = (byte)(raw & 0xFF);
        return profile switch
        {
            0x96 or 0xA3 => "dell-thermal-quiet",
            0x97 or 0xA0 => "dell-thermal-optimal",
            0xA2 => "dell-thermal-cool",
            0x99 or 0xA1 or 0xA4 or 0xAB => "dell-thermal-ultra",
            _ => null
        };
    }

    private static bool TryThermalInformation(byte operation, byte arg1, out uint argr) =>
        TryInvoke("Thermal_Information", PackArgs(operation, arg1), out argr);

    private static bool TryThermalControl(byte operation, byte arg1, out uint argr) =>
        TryInvoke("Thermal_Control", PackArgs(operation, arg1), out argr);

    private static uint PackArgs(byte operation, byte arg1, byte arg2 = 0, byte arg3 = 0) =>
        operation | ((uint)arg1 << 8) | ((uint)arg2 << 16) | ((uint)arg3 << 24);

    private static bool TryInvoke(string method, uint arg2, out uint argr)
    {
        argr = FailureCode;
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM AWCCWmiMethodFunction");
            foreach (ManagementObject obj in searcher.Get())
            {
                using var inParams = obj.GetMethodParameters(method);
                inParams["arg2"] = arg2;
                using var outParams = obj.InvokeMethod(method, inParams, null);
                if (outParams?["argr"] is uint u)
                {
                    argr = u;
                    return true;
                }

                if (outParams?["argr"] is int i)
                {
                    argr = (uint)i;
                    return true;
                }
            }
        }
        catch
        {
            // WMI optional
        }

        return false;
    }
}
