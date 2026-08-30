using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace KlarWin.Services;

public sealed class AiAppGroup
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
}

public sealed class AiProcessRow
{
    public string Name { get; init; } = "";
    public int Count { get; init; }
    public double CpuPercent { get; init; }
    public long RamBytes { get; init; }
    public bool OnGpu { get; init; }
}

public sealed class AiLoadSnapshot
{
    public double SystemCpuPercent { get; init; }
    public long SystemRamBytes { get; init; }
    public double CpuPercent { get; init; }
    public double RamPercent { get; init; }
    public long RamBytes { get; init; }
    public double GpuPercent { get; init; }
    public long GpuMemoryUsedBytes { get; init; }
    public long GpuMemoryTotalBytes { get; init; }
    public string GpuName { get; init; } = "";
    public bool GpuAvailable { get; init; }
    public bool SelectedOnGpu { get; init; }
    public string FilterLabel { get; init; } = "Alle KI";
    public IReadOnlyList<AiAppGroup> AvailableApps { get; init; } = [];
    public IReadOnlyList<AiProcessRow> Rows { get; init; } = [];
}

public sealed class AiLoadService
{
    private static readonly (string Id, string Label, string[] Names)[] Catalog =
    [
        ("cursor", "Cursor", ["cursor"]),
        ("ollama", "Ollama", ["ollama", "ollama app"]),
        ("llama", "Llama", ["llama", "llama-server", "llama.cpp", "llamacpp"]),
        ("lmstudio", "LM Studio", ["lm studio", "lmstudio"]),
        ("chatgpt", "ChatGPT", ["chatgpt", "chatgpt classic"]),
        ("comfy", "ComfyUI", ["comfyui", "comfy"]),
        ("python", "Python (KI)", ["python", "pythonw"])
    ];

    private static readonly string[] PythonHints = ["ollama", "llama", "transformers", "vllm", "comfy", "diffusers", "whisper"];

    private readonly Dictionary<int, (TimeSpan Cpu, DateTime Utc)> _samples = [];
    private AiLoadSnapshot _gpuCache = new();
    private DateTime _gpuCacheUtc = DateTime.MinValue;
    private HashSet<int> _gpuPids = [];

    public AiLoadSnapshot Capture(string filterId, double systemCpuPercent, long systemRamBytes)
    {
        RefreshGpuIfNeeded();
        var now = DateTime.UtcNow;
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return new AiLoadSnapshot { SystemCpuPercent = systemCpuPercent, SystemRamBytes = systemRamBytes };
        }

        var matched = new List<(string GroupId, string Name, int Pid, double Cpu, long Ram)>();
        var seenGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in processes)
        {
            try
            {
                var groupId = MatchGroup(process);
                if (groupId is null) continue;
                seenGroups.Add(groupId);

                var cpu = SampleCpu(process, now);
                matched.Add((groupId, process.ProcessName, process.Id, cpu, process.WorkingSet64));
            }
            catch
            {
                // access denied
            }
            finally
            {
                process.Dispose();
            }
        }

        PruneSamples(matched.Select(m => m.Pid).ToHashSet());

        var filtered = filterId is "" or "all"
            ? matched
            : matched.Where(m => m.GroupId.Equals(filterId, StringComparison.OrdinalIgnoreCase)).ToList();

        var rows = filtered
            .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AiProcessRow
            {
                Name = g.Key,
                Count = g.Count(),
                CpuPercent = g.Sum(x => x.Cpu),
                RamBytes = g.Sum(x => x.Ram),
                OnGpu = g.Any(x => _gpuPids.Contains(x.Pid))
            })
            .OrderByDescending(r => r.CpuPercent)
            .ThenByDescending(r => r.RamBytes)
            .ToList();

        var ram = filtered.Sum(m => m.Ram);
        var apps = Catalog
            .Where(c => seenGroups.Contains(c.Id))
            .Select(c => new AiAppGroup { Id = c.Id, Label = c.Label })
            .ToList();

        var label = filterId is "" or "all"
            ? "Alle KI"
            : Catalog.FirstOrDefault(c => c.Id == filterId).Label ?? filterId;

        return new AiLoadSnapshot
        {
            SystemCpuPercent = systemCpuPercent,
            SystemRamBytes = systemRamBytes,
            CpuPercent = Math.Clamp(filtered.Sum(m => m.Cpu), 0, 100),
            RamPercent = systemRamBytes > 0 ? 100.0 * ram / systemRamBytes : 0,
            RamBytes = ram,
            GpuPercent = _gpuCache.GpuPercent,
            GpuMemoryUsedBytes = _gpuCache.GpuMemoryUsedBytes,
            GpuMemoryTotalBytes = _gpuCache.GpuMemoryTotalBytes,
            GpuName = _gpuCache.GpuName,
            GpuAvailable = _gpuCache.GpuAvailable,
            SelectedOnGpu = filtered.Any(m => _gpuPids.Contains(m.Pid)),
            FilterLabel = label,
            AvailableApps = apps,
            Rows = rows
        };
    }

    private double SampleCpu(Process process, DateTime now)
    {
        TimeSpan cpu;
        try
        {
            cpu = process.TotalProcessorTime;
        }
        catch
        {
            return 0;
        }

        if (_samples.TryGetValue(process.Id, out var previous))
        {
            var seconds = (now - previous.Utc).TotalSeconds;
            if (seconds > 0.2)
            {
                var delta = (cpu - previous.Cpu).TotalSeconds;
                _samples[process.Id] = (cpu, now);
                return Math.Clamp(100.0 * delta / seconds / Environment.ProcessorCount, 0, 100);
            }
        }

        _samples[process.Id] = (cpu, now);
        return 0;
    }

    private void PruneSamples(HashSet<int> living)
    {
        foreach (var pid in _samples.Keys.ToList())
        {
            if (!living.Contains(pid)) _samples.Remove(pid);
        }
    }

    private static string? MatchGroup(Process process)
    {
        var name = process.ProcessName;
        foreach (var entry in Catalog)
        {
            if (!entry.Names.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase)
                                      || name.Contains(n, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (entry.Id == "python")
            {
                return LooksLikePythonAi(process) ? entry.Id : null;
            }

            return entry.Id;
        }

        return null;
    }

    private static bool LooksLikePythonAi(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName ?? "";
            if (PythonHints.Any(h => path.Contains(h, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        catch
        {
            // MainModule often denied
        }

        return false;
    }

    private void RefreshGpuIfNeeded()
    {
        if ((DateTime.UtcNow - _gpuCacheUtc).TotalSeconds < 2 && _gpuCacheUtc != DateTime.MinValue)
        {
            return;
        }

        _gpuCacheUtc = DateTime.UtcNow;
        var smi = FindNvidiaSmi();
        if (smi is null)
        {
            _gpuCache = new AiLoadSnapshot();
            _gpuPids = [];
            return;
        }

        var gpuLine = RunHidden(smi, "--query-gpu=name,utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits");
        var apps = RunHidden(smi, "--query-compute-apps=pid,process_name,used_gpu_memory --format=csv,noheader");
        var pids = new HashSet<int>();
        if (!string.IsNullOrWhiteSpace(apps))
        {
            foreach (var line in apps.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var pidText = line.Split(',')[0].Trim();
                if (int.TryParse(pidText, out var pid)) pids.Add(pid);
            }
        }

        _gpuPids = pids;
        if (string.IsNullOrWhiteSpace(gpuLine))
        {
            _gpuCache = new AiLoadSnapshot { GpuAvailable = false };
            return;
        }

        var parts = gpuLine.Split(',').Select(p => p.Trim()).ToArray();
        _gpuCache = new AiLoadSnapshot
        {
            GpuAvailable = true,
            GpuName = parts.Length > 0 ? parts[0] : "NVIDIA",
            GpuPercent = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var util) ? util : 0,
            GpuMemoryUsedBytes = parts.Length > 2 && long.TryParse(parts[2].Split(' ')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var used) ? used * 1024 * 1024 : 0,
            GpuMemoryTotalBytes = parts.Length > 3 && long.TryParse(parts[3].Split(' ')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var total) ? total * 1024 * 1024 : 0
        };
    }

    private static string? FindNvidiaSmi()
    {
        var fromPath = "nvidia-smi";
        var candidates = new[]
        {
            fromPath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
            @"C:\Windows\System32\nvidia-smi.exe"
        };

        foreach (var candidate in candidates)
        {
            if (candidate == fromPath) return candidate;
            if (File.Exists(candidate)) return candidate;
        }

        return fromPath;
    }

    private static string RunHidden(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return "";
            if (!process.WaitForExit(1500))
            {
                try { process.Kill(true); } catch { /* ignore */ }
                return "";
            }

            return process.StandardOutput.ReadToEnd().Trim();
        }
        catch
        {
            return "";
        }
    }
}
