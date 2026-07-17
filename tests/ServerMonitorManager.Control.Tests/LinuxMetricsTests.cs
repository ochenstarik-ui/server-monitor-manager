using ServerMonitorManager.Agent;
using Xunit;

namespace ServerMonitorManager.Control.Tests;

public sealed class LinuxMetricsTests
{
    [Fact]
    public void ProcParsersHandleRepresentativeLinuxData()
    {
        Assert.Equal(0.42, LinuxMetrics.ParseLoadOne("0.42 0.31 0.20 1/100 123\n"));
        Assert.Equal(1234L, LinuxMetrics.ParseUptimeSeconds("1234.98 567.00\n"));
        Assert.Equal(
            (2L * 1024 * 1024, 512L * 1024),
            LinuxMetrics.ParseMemory(
            [
                "MemTotal:        2048 kB",
                "MemFree:          100 kB",
                "MemAvailable:     512 kB"
            ]));
        Assert.Equal(
            (400L, 600L),
            LinuxMetrics.ParseNetwork(
            [
                "Inter-| Receive | Transmit",
                " face |bytes ...|bytes ...",
                "lo: 10 0 0 0 0 0 0 0 20 0 0 0 0 0 0 0",
                "eth0: 100 0 0 0 0 0 0 0 200 0 0 0 0 0 0 0",
                "wg0: 300 0 0 0 0 0 0 0 400 0 0 0 0 0 0 0"
            ]));
    }

    [Fact]
    public void InvalidMemoryTotalsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            LinuxMetrics.ParseMemory(["MemAvailable: 10 kB"]));
    }
}
