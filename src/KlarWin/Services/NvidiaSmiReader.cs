using System.Diagnostics;

namespace KlarWin.Services;

public static class NvidiaSmiReader
{
    public static IReadOnlyList<TemperatureSensor> Read()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=index,name,temperature.gpu --format=csv,noheader,nounits",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null) return [];
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(4000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return [];

            var sensors = new List<TemperatureSensor>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(',', 3);
                if (parts.Length < 3) continue;
                if (!int.TryParse(parts[0].Trim(), out var index)) continue;
                if (!double.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var temp)) continue;
                if (temp is < -40 or > 150) continue;

                var gpuName = parts[1].Trim().Trim('"');
                sensors.Add(new TemperatureSensor
                {
                    Id = $"nvidia-smi-{index}",
                    Name = string.IsNullOrWhiteSpace(gpuName) ? $"GPU {index}" : gpuName,
                    Celsius = Math.Round(temp, 1)
                });
            }

            return sensors;
        }
        catch
        {
            return [];
        }
    }
}
