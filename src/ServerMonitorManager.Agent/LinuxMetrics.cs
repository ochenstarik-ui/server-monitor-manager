using ServerMonitorManager.Core;

namespace ServerMonitorManager.Agent;

internal static class LinuxMetrics
{
    public static AgentHeartbeat Collect(string nodeId, string version)
    {
        var memory = ReadMemory();
        var disk = new DriveInfo("/");
        var network = ReadNetwork();
        return new AgentHeartbeat(
            nodeId,
            version,
            DateTimeOffset.UtcNow,
            ReadLoadOne(),
            memory.Total - memory.Available,
            memory.Total,
            disk.TotalSize - disk.AvailableFreeSpace,
            disk.TotalSize,
            network.Receive,
            network.Transmit,
            ReadUptimeSeconds(),
            Guid.NewGuid().ToString());
    }

    private static double ReadLoadOne()
    {
        return ParseLoadOne(File.ReadAllText("/proc/loadavg"));
    }

    private static long ReadUptimeSeconds()
    {
        return ParseUptimeSeconds(File.ReadAllText("/proc/uptime"));
    }

    private static (long Total, long Available) ReadMemory()
    {
        return ParseMemory(File.ReadLines("/proc/meminfo"));
    }

    internal static double ParseLoadOne(string contents)
    {
        var value = contents.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static long ParseUptimeSeconds(string contents)
    {
        var value = contents.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return (long)double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static (long Total, long Available) ParseMemory(IEnumerable<string> lines)
    {
        long total = 0;
        long available = 0;
        foreach (var line in lines)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }
            if (parts[0] == "MemTotal:")
            {
                total = long.Parse(parts[1]) * 1024;
            }
            else if (parts[0] == "MemAvailable:")
            {
                available = long.Parse(parts[1]) * 1024;
            }
        }
        if (total <= 0 || available < 0 || available > total)
        {
            throw new InvalidDataException("/proc/meminfo does not contain valid memory totals.");
        }
        return (total, available);
    }

    private static (long Receive, long Transmit) ReadNetwork()
    {
        return ParseNetwork(File.ReadLines("/proc/net/dev"));
    }

    internal static (long Receive, long Transmit) ParseNetwork(IEnumerable<string> lines)
    {
        long receive = 0;
        long transmit = 0;
        foreach (var line in lines.Skip(2))
        {
            var parts = line.Split([':', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 10 || parts[0] == "lo")
            {
                continue;
            }
            receive += long.Parse(parts[1]);
            transmit += long.Parse(parts[9]);
        }
        return (receive, transmit);
    }
}
