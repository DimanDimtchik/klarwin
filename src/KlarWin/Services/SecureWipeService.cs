using System.IO;
using System.Management;
using System.Security.Cryptography;

namespace KlarWin.Services;

public enum WipeMode
{
    ZeroFill = 1,
    Dod3Pass = 3,
    Gutmann35 = 35
}

public sealed class WipeResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public long BytesWritten { get; init; }
}

public sealed class SecureWipeService
{
    public bool LooksLikeSsd(string root)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT MediaType, Model FROM Win32_DiskDrive");
            foreach (ManagementObject drive in searcher.Get())
            {
                var media = drive["MediaType"]?.ToString() ?? "";
                var model = drive["Model"]?.ToString() ?? "";
                if (media.Contains("SSD", StringComparison.OrdinalIgnoreCase)
                    || model.Contains("SSD", StringComparison.OrdinalIgnoreCase)
                    || media.Contains("Solid", StringComparison.OrdinalIgnoreCase))
                {
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

    public WipeResult WipeFreeSpace(string rootPath, WipeMode mode, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var root = Path.GetPathRoot(rootPath)?.TrimEnd('\\') + "\\";
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return new WipeResult { Success = false, Message = "Laufwerk nicht gefunden." };
        }

        var passes = (int)mode;
        long writtenTotal = 0;
        var workDir = Path.Combine(root, "KlarWinWipe");
        Directory.CreateDirectory(workDir);

        try
        {
            for (var pass = 1; pass <= passes; pass++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Durchlauf {pass}/{passes} auf {root} …");
                writtenTotal += FillFreeSpace(workDir, pass, mode, progress, cancellationToken);
            }

            return new WipeResult
            {
                Success = true,
                BytesWritten = writtenTotal,
                Message = mode == WipeMode.ZeroFill
                    ? $"Freier Speicher mit Nullen überschrieben ({CleanupService.FormatBytes(writtenTotal)})."
                    : $"Freier Speicher in {passes} Durchläufen überschrieben ({CleanupService.FormatBytes(writtenTotal)})."
            };
        }
        catch (OperationCanceledException)
        {
            return new WipeResult { Success = false, Message = "Abgebrochen.", BytesWritten = writtenTotal };
        }
        catch (Exception ex)
        {
            return new WipeResult { Success = false, Message = ex.Message, BytesWritten = writtenTotal };
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDir))
                {
                    Directory.Delete(workDir, true);
                }
            }
            catch
            {
                // leftover wipe files may remain until reboot
            }
        }
    }

    private static long FillFreeSpace(string workDir, int pass, WipeMode mode, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        long written = 0;
        var index = 0;
        const int chunkSize = 64 * 1024 * 1024;
        var buffer = new byte[chunkSize];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FillBuffer(buffer, pass, mode);
            var path = Path.Combine(workDir, $"pass{pass}_{index:D4}.tmp");
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, chunkSize, FileOptions.WriteThrough);
                var free = new DriveInfo(Path.GetPathRoot(workDir)!).AvailableFreeSpace;
                if (free < chunkSize)
                {
                    if (free > 4096)
                    {
                        var small = new byte[(int)Math.Min(free - 4096, chunkSize)];
                        FillBuffer(small, pass, mode);
                        stream.Write(small, 0, small.Length);
                        written += small.Length;
                    }
                    break;
                }

                stream.Write(buffer, 0, buffer.Length);
                written += buffer.Length;
                index++;
                if (index % 8 == 0)
                {
                    progress?.Report($"Durchlauf {pass}: {CleanupService.FormatBytes(written)} geschrieben …");
                }
            }
            catch (IOException)
            {
                break;
            }
        }

        foreach (var file in Directory.EnumerateFiles(workDir, $"pass{pass}_*.tmp"))
        {
            try { File.Delete(file); } catch { /* ignore */ }
        }

        return written;
    }

    private static void FillBuffer(byte[] buffer, int pass, WipeMode mode)
    {
        if (mode == WipeMode.ZeroFill || (mode == WipeMode.Dod3Pass && pass == 3) || (mode == WipeMode.Gutmann35 && pass is 1 or 35))
        {
            Array.Clear(buffer);
            return;
        }

        if (mode == WipeMode.Dod3Pass && pass == 1)
        {
            Array.Fill(buffer, (byte)0xFF);
            return;
        }

        RandomNumberGenerator.Fill(buffer);
    }
}
