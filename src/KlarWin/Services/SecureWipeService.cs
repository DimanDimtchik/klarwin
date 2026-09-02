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
                    ? $"Freier Speicher mit Nullen überschrieben ({ByteSizeFormatter.Format(writtenTotal)})."
                    : $"Freier Speicher in {passes} Durchläufen überschrieben ({ByteSizeFormatter.Format(writtenTotal)})."
            };
        }
        catch (OperationCanceledException)
        {
            return new WipeResult { Success = false, Message = "Abgebrochen.", BytesWritten = writtenTotal };
        }
        catch (Exception ex)
        {
            return new WipeResult { Success = false, Message = UserFriendlyErrors.Translate(ex), BytesWritten = writtenTotal };
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
                    progress?.Report($"Durchlauf {pass}: {ByteSizeFormatter.Format(written)} geschrieben …");
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

    /// <summary>Overwrite and delete a single file or folder tree.</summary>
    public WipeResult SecureDeletePath(string targetPath, WipeMode mode, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetPath) || (!File.Exists(targetPath) && !Directory.Exists(targetPath)))
        {
            return new WipeResult { Success = false, Message = "Datei oder Ordner nicht gefunden." };
        }

        var passes = (int)mode;
        long written = 0;
        try
        {
            if (File.Exists(targetPath))
            {
                written += OverwriteFile(targetPath, passes, mode, progress, cancellationToken);
                File.SetAttributes(targetPath, FileAttributes.Normal);
                File.Delete(targetPath);
                return new WipeResult { Success = true, BytesWritten = written, Message = $"„{Path.GetFileName(targetPath)}“ sicher gelöscht." };
            }

            foreach (var file in Directory.EnumerateFiles(targetPath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                written += OverwriteFile(file, passes, mode, progress, cancellationToken);
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }

            Directory.Delete(targetPath, true);
            return new WipeResult { Success = true, BytesWritten = written, Message = $"Ordner „{Path.GetFileName(targetPath.TrimEnd('\\'))}“ sicher gelöscht." };
        }
        catch (OperationCanceledException)
        {
            return new WipeResult { Success = false, Message = "Abgebrochen.", BytesWritten = written };
        }
        catch (Exception ex)
        {
            return new WipeResult { Success = false, Message = UserFriendlyErrors.Translate(ex), BytesWritten = written };
        }
    }

    private static long OverwriteFile(string path, int passes, WipeMode mode, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        long written = 0;
        var length = new FileInfo(path).Length;
        if (length <= 0) return 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        var buffer = new byte[Math.Min(1024 * 1024, Math.Max(length, 4096))];
        for (var pass = 1; pass <= passes; pass++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Überschreibe {Path.GetFileName(path)} — Durchlauf {pass}/{passes}");
            stream.Seek(0, SeekOrigin.Begin);
            var remaining = length;
            FillBuffer(buffer, pass, mode);
            while (remaining > 0)
            {
                var chunk = (int)Math.Min(buffer.Length, remaining);
                stream.Write(buffer, 0, chunk);
                written += chunk;
                remaining -= chunk;
            }

            stream.Flush(true);
        }

        return written;
    }
}
