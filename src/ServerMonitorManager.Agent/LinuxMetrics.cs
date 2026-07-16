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
        var value = File.ReadAllText("/proc/loadavg").Split(' ', 2)[0];
        return double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long ReadUptimeSeconds()
    {
        var value = File.ReadAllText("/proc/uptime").Split(' ', 2)[0];
        return (long)double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static (long Total, long Available) ReadMemory()
    {
        long total = 0;
        long available = 0;
        foreach (var line in File.ReadLines("/proc/meminfo"))
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
        return (total, available);
    }

    private static (long Receive, long Transmit) ReadNetwork()
    {
        long receive = 0;
        long transmit = 0;
        foreach (var line in File.ReadLines("/proc/net/dev").Skip(2))
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
