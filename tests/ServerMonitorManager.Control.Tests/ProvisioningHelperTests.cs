using System.Text.Json;
using ServerMonitorManager.Core;
using ServerMonitorManager.Provisioning.Helper;
using Xunit;

namespace ServerMonitorManager.Control.Tests;

public sealed class ProvisioningHelperTests
{
    [Fact]
    public void HelperRejectsEveryActionOutsideFixedAllowlist()
    {
        using var document = JsonDocument.Parse("{}");
        var response = ProvisioningHelperServer.Execute(new ProvisioningHelperRequest(
            "1", new string('a', 32), "shell", 1,
            ProvisioningActionCatalog.PreflightModuleHash, document.RootElement.Clone()));

        Assert.False(response.Success);
        Assert.Equal("action.denied", response.Code);
        Assert.Null(response.Preflight);
    }

    [Fact]
    public void HelperAcceptsOnlyEmptyPreflightSchemaOne()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var document = JsonDocument.Parse("{}");
        var response = ProvisioningHelperServer.Execute(new ProvisioningHelperRequest(
            "1", new string('b', 32), "preflight", 1,
            ProvisioningActionCatalog.PreflightModuleHash, document.RootElement.Clone()));

        Assert.True(response.Success);
        Assert.Equal("preflight.completed", response.Code);
        Assert.NotNull(response.Preflight);
        Assert.NotEmpty(response.Preflight.OperatingSystem);
        Assert.NotEmpty(response.Preflight.Architecture);
    }

    [Fact]
    public void HelperContractRejectsUnknownJsonMembers()
    {
        const string json = """
            {"protocolVersion":"1","jobId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
             "actionType":"preflight","schemaVersion":1,
             "moduleHash":"2dc48fb4528a291221954fc2dd3478d431b66fe34228f29684ce1648dbe2f32b",
             "parameters":{},"command":"id"}
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(json, SmmJsonContext.Default.ProvisioningHelperRequest));
    }

    [Fact]
    public void BaseInstallSchemaRejectsCommandText()
    {
        const string json = """
            {"timezone":"UTC","locale":"en_US.UTF-8","aptUpdate":true,"aptUpgrade":false,
             "packageCatalogVersion":1,"packageGroupIds":["core"],"swapMode":"disabled",
             "swapSizeMiB":null,"vmSwappiness":60,"enableUnattendedUpgrades":true,
             "rebootPolicy":"never","command":"id"}
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(json, SmmJsonContext.Default.SystemBaseInstallParameters));
    }

    [Fact]
    public void HelperBuildsDeterministicBaseInstallPlanWithoutCommands()
    {
        var parameters = new SystemBaseInstallParameters(
            "UTC", "en_US.UTF-8", true, false, 1,
            ["development", "core"], "disabled", null, 60, true, "never");
        var json = JsonSerializer.SerializeToElement(
            parameters, SmmJsonContext.Default.SystemBaseInstallParameters);
        var response = ProvisioningHelperServer.Execute(new ProvisioningHelperRequest(
            "1", new string('c', 32), "system.base-install", 1,
            ProvisioningActionCatalog.SystemBaseInstallModuleHash, json));

        Assert.True(response.Success);
        Assert.Equal("system.base-install.plan-ready", response.Code);
        Assert.Null(response.Preflight);
        Assert.Equal(
            ["ca-certificates", "curl", "jq", "build-essential", "git"],
            response.BaseInstallPlan!.Packages);
        Assert.Equal("never", response.BaseInstallPlan.RebootPolicy);
    }
}
