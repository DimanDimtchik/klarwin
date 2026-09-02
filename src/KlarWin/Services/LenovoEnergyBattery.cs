using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KlarWin.Services;

/// <summary>
/// Lenovo EnergyDrv battery charge mode via installed OEM driver (\\.\EnergyDrv).
/// Protocol from LenovoLegionToolkit BatteryFeature.
/// </summary>
internal static class LenovoEnergyBattery
{
    private const uint IoctlBatteryChargeMode = 0x831020F8;
    private const string DevicePath = @"\\.\EnergyDrv";

    public static bool IsAvailable()
    {
        using var handle = OpenDevice();
        return handle is not null && !handle.IsInvalid;
    }

    public static bool TryReadModeId(out string? modeId)
    {
        modeId = null;
        if (!TryQuery(out var raw))
        {
            return false;
        }

        modeId = MapRawState(raw);
        return modeId is not null;
    }

    public static bool TryWriteModeId(string optionId)
    {
        var current = TryQuery(out var raw) ? MapRawState(raw) : null;
        var codes = BuildWriteCodes(optionId, current);
        if (codes is null || codes.Length == 0)
        {
            return false;
        }

        using var handle = OpenDevice();
        if (handle is null || handle.IsInvalid)
        {
            return false;
        }

        foreach (var code in codes)
        {
            if (!SendIoctl(handle, code, out _))
            {
                return false;
            }
        }

        SyncLenovoRegistry(optionId);
        return true;
    }

    private static uint[]? BuildWriteCodes(string optionId, string? currentModeId)
    {
        var lastConservation = currentModeId == "dell-charge-ac";
        var lastRapid = currentModeId == "dell-charge-express";

        return optionId switch
        {
            "dell-charge-ac" => lastRapid ? [0x8u, 0x3u] : [0x3u],
            "dell-charge-standard" or "dell-charge-adaptive" => lastConservation ? [0x5u] : [0x8u],
            "dell-charge-express" => lastConservation ? [0x5u, 0x7u] : [0x7u],
            _ => null
        };
    }

    private static string? MapRawState(uint rawState)
    {
        var reversed = BinaryPrimitives.ReverseEndianness(rawState);

        if (GetNthBit(reversed, 17))
        {
            return GetNthBit(reversed, 26) ? "dell-charge-express" : "dell-charge-standard";
        }

        if (GetNthBit(reversed, 29))
        {
            return "dell-charge-ac";
        }

        return null;
    }

    private static bool TryQuery(out uint rawState)
    {
        rawState = 0;
        using var handle = OpenDevice();
        if (handle is null || handle.IsInvalid)
        {
            return false;
        }

        return SendIoctl(handle, 0xFF, out rawState);
    }

    private static SafeFileHandle? OpenDevice()
    {
        try
        {
            return CreateFile(
                DevicePath,
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
        }
        catch
        {
            return null;
        }
    }

    private static bool SendIoctl(SafeFileHandle handle, uint input, out uint output)
    {
        output = 0;
        return DeviceIoControl(
            handle,
            IoctlBatteryChargeMode,
            ref input,
            sizeof(uint),
            out output,
            sizeof(uint),
            out _,
            IntPtr.Zero);
    }

    private static void SyncLenovoRegistry(string optionId)
    {
        var text = optionId switch
        {
            "dell-charge-standard" or "dell-charge-adaptive" => "Normal",
            "dell-charge-express" => "Quick",
            "dell-charge-ac" => "Storage",
            _ => null
        };
        if (text is null) return;

        try
        {
            Microsoft.Win32.Registry.SetValue(
                @"HKEY_CURRENT_USER\Software\Lenovo\VantageService\AddinData\IdeaNotebookAddin",
                "BatteryChargeMode",
                text,
                Microsoft.Win32.RegistryValueKind.String);
        }
        catch
        {
            // optional mirror
        }
    }

    private static bool GetNthBit(uint value, int bit) => ((value >> bit) & 1) == 1;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref uint lpInBuffer,
        uint nInBufferSize,
        out uint lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);
}
