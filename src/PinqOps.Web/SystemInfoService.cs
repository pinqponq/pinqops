using System.Runtime.InteropServices;

namespace PinqOps.Web;

/// <summary>Host-level facts for the System panel: uptime, load, memory, disk.</summary>
public sealed class SystemInfoService
{
    public object GetInfo()
    {
        var (memTotalKb, memAvailableKb) = ReadMemInfo();
        var (diskTotal, diskFree) = ReadRootDisk();

        return new
        {
            hostname = Environment.MachineName,
            os = ReadOsPrettyName() ?? RuntimeInformation.OSDescription,
            kernel = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.OSArchitecture.ToString(),
            uptimeSeconds = ReadUptimeSeconds(),
            loadAverage = ReadLoadAverage(),
            memTotalKb,
            memAvailableKb,
            diskTotalBytes = diskTotal,
            diskFreeBytes = diskFree,
            serverTimeUtc = DateTimeOffset.UtcNow,
        };
    }

    private static double? ReadUptimeSeconds()
    {
        try
        {
            var first = File.ReadAllText("/proc/uptime").Split(' ')[0];
            return double.TryParse(first, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                ? seconds
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static double[]? ReadLoadAverage()
    {
        try
        {
            var parts = File.ReadAllText("/proc/loadavg").Split(' ');
            return parts.Length >= 3
                ?
                [
                    double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                    double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                    double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture),
                ]
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private static (long? TotalKb, long? AvailableKb) ReadMemInfo()
    {
        try
        {
            long? total = null, available = null;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    total = ParseKb(line);
                }
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                {
                    available = ParseKb(line);
                }

                if (total is not null && available is not null)
                {
                    break;
                }
            }

            return (total, available);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }

        static long? ParseKb(string line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && long.TryParse(parts[1], out var kb) ? kb : null;
        }
    }

    /// <summary>Free bytes on the root filesystem, or null when unknown.</summary>
    public long? RootFreeBytes() => ReadRootDisk().Free;

    private static (long? Total, long? Free) ReadRootDisk()
    {
        try
        {
            var root = new DriveInfo("/");
            return (root.TotalSize, root.AvailableFreeSpace);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null);
        }
    }

    private static string? ReadOsPrettyName()
    {
        try
        {
            foreach (var line in File.ReadLines("/etc/os-release"))
            {
                if (line.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                {
                    return line["PRETTY_NAME=".Length..].Trim('"');
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }
}
