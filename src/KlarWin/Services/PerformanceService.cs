using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace KlarWin.Services;

public sealed class PerformanceSnapshot
{
    public double CpuPercent { get; init; }
    public double RamPercent { get; init; }
    public long RamUsedBytes { get; init; }
    public long RamTotalBytes { get; init; }
    public double DiskFreePercent { get; init; }
    public long DiskFreeBytes { get; init; }
    public long DiskTotalBytes { get; init; }
    public string DiskName { get; init; } = "C:";
}

public sealed class PerformanceService : IDisposable
{
    private long _previousIdle;
    private long _previousKernel;
    private long _previousUser;
    private bool _hasSample;

    public PerformanceSnapshot Capture()
    {
        var cpu = ReadCpu();
        var memory = ReadMemory();
        var disk = ReadSystemDisk();

        return new PerformanceSnapshot
        {
            CpuPercent = cpu,
            RamPercent = memory.Percent,
            RamUsedBytes = memory.Used,
            RamTotalBytes = memory.Total,
            DiskFreePercent = disk.FreePercent,
            DiskFreeBytes = disk.Free,
            DiskTotalBytes = disk.Total,
            DiskName = disk.Name
        };
    }

    private double ReadCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return 0;
        }

        var idleTime = FileTimeToInt64(idle);
        var kernelTime = FileTimeToInt64(kernel);
        var userTime = FileTimeToInt64(user);

        if (!_hasSample)
        {
            _previousIdle = idleTime;
            _previousKernel = kernelTime;
            _previousUser = userTime;
            _hasSample = true;
            return 0;
        }

        var idleDelta = idleTime - _previousIdle;
        var kernelDelta = kernelTime - _previousKernel;
        var userDelta = userTime - _previousUser;
        var total = kernelDelta + userDelta;

        _previousIdle = idleTime;
        _previousKernel = kernelTime;
        _previousUser = userTime;

        if (total <= 0)
        {
            return 0;
        }

        var busy = total - idleDelta;
        return Math.Clamp(100.0 * busy / total, 0, 100);
    }

    private static (double Percent, long Used, long Total) ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0)
        {
            return (0, 0, 0);
        }

        var used = (long)(status.TotalPhys - status.AvailPhys);
        var percent = 100.0 * used / status.TotalPhys;
        return (percent, used, (long)status.TotalPhys);
    }

    private static (string Name, double FreePercent, long Free, long Total) ReadSystemDisk()
    {
        var drive = DriveInfo.GetDrives()
            .FirstOrDefault(d => d.IsReady && d.Name.StartsWith("C", StringComparison.OrdinalIgnoreCase))
            ?? DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady);

        if (drive is null || drive.TotalSize <= 0)
        {
            return ("—", 0, 0, 0);
        }

        return (drive.Name.TrimEnd('\\'), 100.0 * drive.AvailableFreeSpace / drive.TotalSize, drive.AvailableFreeSpace, drive.TotalSize);
    }

    public void Dispose()
    {
    }

    private static long FileTimeToInt64(FileTime time) => ((long)time.High << 32) | time.Low;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}
