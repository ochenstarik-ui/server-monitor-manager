using Microsoft.Extensions.Configuration;
using ServerMonitorManager.Agent;
using Xunit;

namespace ServerMonitorManager.Control.Tests;

public sealed class AgentConfigurationTests
{
    [Fact]
    public void TryBindFailsClosedWhenNodeEnvironmentValueDidNotBind()
    {
        var configuration = new ConfigurationBuilder().Build();

        var success = AgentConfiguration.TryBind(
            configuration,
            environmentControlUrl: null,
            environmentNodeId: "expected-node",
            out _,
            out var error);

        Assert.False(success);
        Assert.Contains("SMM_NodeId", error, StringComparison.Ordinal);
        Assert.DoesNotContain("expected-node", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryBindFailsClosedWhenControlUrlEnvironmentValueDidNotBind()
    {
        var configuration = new ConfigurationBuilder().Build();

        var success = AgentConfiguration.TryBind(
            configuration,
            environmentControlUrl: "https://control.example:7443",
            environmentNodeId: null,
            out _,
            out var error);

        Assert.False(success);
        Assert.Contains("SMM_ControlUrl", error, StringComparison.Ordinal);
        Assert.DoesNotContain("control.example", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryBindAcceptsMatchingEnvironmentValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NodeId"] = "expected-node",
                ["ControlUrl"] = "https://control.example:7443"
            })
            .Build();

        var success = AgentConfiguration.TryBind(
            configuration,
            environmentControlUrl: "https://control.example:7443",
            environmentNodeId: "expected-node",
            out var options,
            out var error);

        Assert.True(success, error);
        Assert.Equal("expected-node", options.NodeId);
        Assert.Equal(new Uri("https://control.example:7443"), options.ControlUrl);
        Assert.Null(error);
    }

    [Fact]
    public void TryBindReturnsNonSecretErrorWhenControlUrlCannotBind()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ControlUrl"] = "not a URL"
            })
            .Build();

        var success = AgentConfiguration.TryBind(
            configuration,
            environmentControlUrl: "not a URL",
            environmentNodeId: null,
            out _,
            out var error);

        Assert.False(success);
        Assert.Equal("Agent configuration binding failed for SMM_ControlUrl.", error);
        Assert.DoesNotContain("not a URL", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://control.example:7443")]
    [InlineData("https://user@control.example:7443")]
    [InlineData("https://control.example:7443/path")]
    [InlineData("https://control.example:7443?query=1")]
    [InlineData("https://control.example:7443#fragment")]
    [InlineData("https://control.example:0")]
    public void TryBindRejectsUnsafeControlUrlsWithoutDisclosingThem(string controlUrl)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ControlUrl"] = controlUrl
            })
            .Build();

        var success = AgentConfiguration.TryBind(
            configuration,
            environmentControlUrl: controlUrl,
            environmentNodeId: null,
            out _,
            out var error);

        Assert.False(success);
        Assert.Equal(AgentConfiguration.ControlUrlError, error);
        Assert.DoesNotContain(controlUrl, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://control.example")]
    [InlineData("https://control.example:7443")]
    [InlineData("https://127.0.0.1:7443/")]
    [InlineData("https://[2001:db8::1]:7443")]
    public void TryBindAcceptsStructurallySafeHttpsControlUrls(string controlUrl)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ControlUrl"] = controlUrl
            })
            .Build();

        var success = AgentConfiguration.TryBind(
            configuration,
            environmentControlUrl: controlUrl,
            environmentNodeId: null,
            out _,
            out var error);

        Assert.True(success, error);
    }
}
