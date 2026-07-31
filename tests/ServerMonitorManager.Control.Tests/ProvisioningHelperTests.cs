using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ServerMonitorManager.Agent;
using ServerMonitorManager.Core;
using ServerMonitorManager.Provisioning.Helper;
using Xunit;

namespace ServerMonitorManager.Control.Tests;

public sealed class ProvisioningHelperTests
{
    [Fact]
    public async Task ConfiguredAgentUidMustMatchTheInstalledAgentUser()
    {
        var path = Path.Combine(Path.GetTempPath(), $"smm-passwd-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(
            path,
            "root:x:0:0:root:/root:/bin/bash\n"
            + "ochenstarik-smm-agent:x:1234:1234::/var/lib/ochenstarik-server-monitor-manager:/usr/sbin/nologin\n",
            TestContext.Current.CancellationToken);
        try
        {
            Assert.True(ProvisioningAgentIdentity.MatchesConfiguredUid(path, 1234));
            Assert.False(ProvisioningAgentIdentity.MatchesConfiguredUid(path, 1235));
        }
        finally
        {
            File.Delete(path);
        }
    }

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
    public void HelperRejectsMissingJobIdWithoutThrowing()
    {
        using var document = JsonDocument.Parse("{}");
        var response = ProvisioningHelperServer.Execute(new ProvisioningHelperRequest(
            "1", null!, "preflight", 1,
            ProvisioningActionCatalog.PreflightModuleHash, document.RootElement.Clone()));

        Assert.False(response.Success);
        Assert.Equal("request.invalid-job", response.Code);
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

    [Fact]
    public async Task ConfirmedTimezoneOnlyPlanUsesAllowlistedBinaryCreatesBackupAndVerifies()
    {
        using var authority = CreateAuthority(out var signingKey);
        using (signingKey)
        {
            var plan = CreateTimezoneOnlyPlan("Europe/Berlin");
            var grant = SignGrant(authority, signingKey, plan);
            var events = new List<string>();
            var files = new FakeFileSystem(events);
            files.Files.Add("/usr/share/zoneinfo/Europe/Berlin");
            var process = new FakeProcessRunner(events,
                new(0, "UTC\n", ""),
                new(0, "", ""),
                new(0, "Europe/Berlin\n", ""));
            var executor = new TimezoneProvisioningExecutor(
                authority, "home", files, process, TimeProvider.System, "/var/lib/ochenstarik-server-monitor-manager/provisioning/rollback");

            var result = await executor.ExecuteAsync(
                CreateExecutionRequest(plan, grant), TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.True(result.Changed);
            Assert.True(result.Verified);
            Assert.False(result.RollbackAttempted);
            Assert.Equal("Europe/Berlin", result.ObservedTimezone);
            Assert.Equal(2, files.Writes.Count);
            Assert.True(events.IndexOf("write-backup") < events.IndexOf("process:set-timezone Europe/Berlin"));
            Assert.All(process.Calls, call => Assert.Equal("/usr/bin/timedatectl", call.FileName));
            Assert.Equal(
                [
                    "show --property=Timezone --value",
                    "set-timezone Europe/Berlin",
                    "show --property=Timezone --value"
                ],
                process.Calls.Select(call => string.Join(' ', call.Arguments)));
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task ExecutionDispatchRejectsNonObjectParametersBeforeMutation(string parametersJson)
    {
        using var authority = CreateAuthority(out var signingKey);
        using (signingKey)
        using (var parameters = JsonDocument.Parse(parametersJson))
        {
            var plan = CreateTimezoneOnlyPlan("Europe/Berlin");
            var events = new List<string>();
            var files = new FakeFileSystem(events);
            files.Files.Add("/usr/share/zoneinfo/Europe/Berlin");
            var process = new FakeProcessRunner(events,
                new(0, "UTC\n", ""),
                new(0, "", ""),
                new(0, "Europe/Berlin\n", ""));
            var executor = new TimezoneProvisioningExecutor(
                authority, "home", files, process, TimeProvider.System,
                "/var/lib/ochenstarik-server-monitor-manager/provisioning/rollback");
            var server = new ProvisioningHelperServer("unused", 0, executor);
            var request = CreateExecutionRequest(plan, SignGrant(authority, signingKey, plan)) with
            {
                Parameters = parameters.RootElement.Clone()
            };
            var dispatch = typeof(ProvisioningHelperServer).GetMethod(
                "ExecuteRequestAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;

            var task = (Task<ProvisioningHelperResponse>)dispatch.Invoke(
                server,
                [request, TestContext.Current.CancellationToken])!;
            var response = await task;

            Assert.False(response.Success);
            Assert.Equal("action.denied", response.Code);
            Assert.Empty(files.Writes);
            Assert.Empty(process.Calls);
        }
    }

    [Fact]
    public async Task ValidNoOpTimezoneCompletesOnlyAfterFactualVerification()
    {
        using var authority = CreateAuthority(out var signingKey);
        using (signingKey)
        {
            var plan = CreateTimezoneOnlyPlan("UTC");
            var process = new FakeProcessRunner(
                [], new ProvisioningProcessResult(0, "UTC\n", ""));
            var files = new FakeFileSystem([]);
            files.Files.Add("/usr/share/zoneinfo/UTC");
            var executor = new TimezoneProvisioningExecutor(
                authority, "home", files, process, TimeProvider.System, "/var/lib/ochenstarik-server-monitor-manager/provisioning/rollback");

            var result = await executor.ExecuteAsync(
                CreateExecutionRequest(plan, SignGrant(authority, signingKey, plan)),
                TestContext.Current.CancellationToken);

            Assert.True(result.Success);
            Assert.False(result.Changed);
            Assert.True(result.Verified);
            Assert.Single(files.Writes);
            Assert.Single(process.Calls);
        }
    }

    [Fact]
    public async Task ConsumedGrantCannotBeReplayedEvenAfterVerifiedNoOp()
    {
        using var authority = CreateAuthority(out var signingKey);
        using (signingKey)
        {
            var plan = CreateTimezoneOnlyPlan("UTC");
            var grant = SignGrant(authority, signingKey, plan);
            var files = new FakeFileSystem([]);
            files.Files.Add("/usr/share/zoneinfo/UTC");
            var firstProcess = new FakeProcessRunner(
                [], new ProvisioningProcessResult(0, "UTC\n", ""));
            var executor = new TimezoneProvisioningExecutor(
                authority, "home", files, firstProcess, TimeProvider.System,
                "/var/lib/ochenstarik-server-monitor-manager/provisioning/rollback");

            var first = await executor.ExecuteAsync(
                CreateExecutionRequest(plan, grant), TestContext.Current.CancellationToken);
            var secondProcess = new FakeProcessRunner([]);
            var replayExecutor = new TimezoneProvisioningExecutor(
                authority, "home", files, secondProcess, TimeProvider.System,
                "/var/lib/ochenstarik-server-monitor-manager/provisioning/rollback");
            var replay = await replayExecutor.ExecuteAsync(
                CreateExecutionRequest(plan, grant), TestContext.Current.CancellationToken);

            Assert.True(first.Success);
            Assert.False(replay.Success);
            Assert.Equal("execution.grant-consumed", replay.Code);
            Assert.Empty(secondProcess.Calls);
        }
    }

    [Fact]
    public async Task ForgedMismatchedAndExpiredGrantsCauseZeroMutation()
    {
        using var authority = CreateAuthority(out var signingKey);
        using (signingKey)
        {
            var plan = CreateTimezoneOnlyPlan("Europe/Berlin");
            var valid = SignGrant(authority, signingKey, plan);
            var invalid = new[]
            {
                valid with { Signature = valid.Signature[..^1] + (valid.Signature[^1] == 'A' ? "B" : "A") },
                valid with { JobId = new string('f', 32) },
                valid with { NodeId = "other-node" },
                valid with { PlanSha256 = new string('0', 64) },
                SignGrant(authority, signingKey, plan, DateTimeOffset.UtcNow.AddMinutes(-10))
            };

            foreach (var grant in invalid)
            {
                var files = new FakeFileSystem([]);
                files.Files.Add("/usr/share/zoneinfo/Europe/Berlin");
                var process = new FakeProcessRunner([]);
                var executor = new TimezoneProvisioningExecutor(
                    authority, "home", files, process, TimeProvider.System, "/var/lib/ochenstarik-server-monitor-manager/provisioning/rollback");

                var result = await executor.ExecuteAsync(
                    CreateExecutionRequest(plan, grant), TestContext.Current.CancellationToken);

                Assert.False(result.Success);
                Assert.Equal("execution.authorization-denied", result.Code);
                Assert.Empty(files.Writes);
                Assert.Empty(process.Calls);
            }
        }
    }

    [Fact]
    public async Task ValidGrantForAnotherNodeIsRejectedBeforeMutation()
    {
        using var authority = CreateAuthority(out var signingKey);
        using (signingKey)
        {
            var plan = CreateTimezoneOnlyPlan("Europe/Berlin");
            var otherNodeGrant = SignGrant(authority, signingKey, plan, nodeId: "other-node");
            var localGrant = SignGrant(authority, signingKey, plan);
            var requests = new[]
            {
                CreateExecutionRequest(plan, otherNodeGrant) with
                {
                    Execution = new ProvisioningBaseInstallExecutionAuthorization(
                        "other-node", plan, otherNodeGrant)
                },
                CreateExecutionRequest(plan, localGrant) with
                {
                    Execution = new ProvisioningBaseInstallExecutionAuthorization(
                        "other-node", plan, localGrant)
                }
            };

            foreach (var request in requests)
            {
                var files = new FakeFileSystem([]);
                files.Files.Add("/usr/share/zoneinfo/Europe/Berlin");
                var process = new FakeProcessRunner([]);
                var executor = new TimezoneProvisioningExecutor(
                    authority, "home", files, process, TimeProvider.System,
                    "/var/lib/ochenstarik-server-monitor-manager/provisioning/rollback");

                var result = await executor.ExecuteAsync(
                    request, TestContext.Current.CancellationToken);

                Assert.False(result.Success);
                Assert.Equal("execution.authorization-denied", result.Code);
                Assert.Empty(files.Writes);
                Assert.Empty(process.Calls);
            }
        }
    }

    [Fact]
    public async Task UnsupportedBaseInstallFieldsCauseZeroMutation()
    {
        using var authority = CreateAuthority(out var signingKey);
        using (signingKey)
        {
            var baseline = CreateTimezoneOnlyPlan("Europe/Berlin");
            var unsupportedPlans = new[]
            {
                baseline with { Locale = "en_US.UTF-8" },
                baseline with { AptUpdate = true },
                baseline with { AptUpgrade = true },
                baseline with { Packages = ["curl"] },
                baseline with { SwapMode = "automatic" },
                baseline with { VmSwappiness = 10 },
                baseline with { EnableUnattendedUpgrades = true },
                baseline with { RebootPolicy = "always" }
            };

            foreach (var plan in unsupportedPlans)
            {
                var files = new FakeFileSystem([]);
                files.Files.Add("/usr/share/zoneinfo/Europe/Berlin");
                var process = new FakeProcessRunner([]);
                var executor = new TimezoneProvisioningExecutor(
                    authority, "home", files, process, TimeProvider.System, "/var/lib/ochenstarik-server-monitor-manager/provisioning/rollback");

                var result = await executor.ExecuteAsync(
                    CreateExecutionRequest(plan, SignGrant(authority, signingKey, plan)),
                    TestContext.Current.CancellationToken);

                Assert.False(result.Success);
                Assert.Equal("system.base-install.unsupported-fields", result.Code);
                Assert.Empty(files.Writes);
                Assert.Empty(process.Calls);
            }
        }
    }

    [Fact]
    public void AgentExecutionRequestUsesTheControlAuthorizedPlanAndGrant()
    {
        using var authority = CreateAuthority(out var signingKey);
        using (signingKey)
        using (var parameters = JsonDocument.Parse("{}"))
        {
            var plan = CreateTimezoneOnlyPlan("Europe/Berlin");
            var grant = SignGrant(authority, signingKey, plan);
            var authorization = new ProvisioningBaseInstallExecutionAuthorization("home", plan, grant);
            var job = new ProvisioningJob(
                new string('d', 32), "home", "system.base-install", 1,
                parameters.RootElement.Clone(), ProvisioningJobStates.Running, true,
                "Change timezone", "operator", DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30),
                DateTimeOffset.UtcNow, null, 3, 40, "execute", null);

            var request = ProvisioningHelperClient.CreateBaseInstallExecutionRequest(job, authorization);

            Assert.Same(authorization, request.Execution);
            Assert.Equal(job.Id, request.JobId);
            Assert.Equal(job.ActionType, request.ActionType);
            Assert.Equal(ProvisioningActionCatalog.SystemBaseInstallModuleHash, request.ModuleHash);
            Assert.Equal(JsonValueKind.Object, request.Parameters.ValueKind);
        }
    }

    [Fact]
    public void AgentRejectsInconsistentOrUnverifiedHelperSuccess()
    {
        using var authority = CreateAuthority(out var signingKey);
        using (signingKey)
        {
            var plan = CreateTimezoneOnlyPlan("UTC");
            var authorization = new ProvisioningBaseInstallExecutionAuthorization(
                "home", plan, SignGrant(authority, signingKey, plan));
            var unverified = new ProvisioningBaseInstallExecutionResult(
                true, "bad", "bad", false, false, false, false, "UTC");
            var contradictory = new ProvisioningHelperResponse(
                false, "bad", "bad", null, null,
                unverified with { Verified = true });

            Assert.Throws<InvalidDataException>(() =>
                ProvisioningHelperClient.ValidateBaseInstallExecutionResponse(
                    new ProvisioningHelperResponse(true, "bad", "bad", null, null, unverified),
                    authorization));
            Assert.Throws<InvalidDataException>(() =>
                ProvisioningHelperClient.ValidateBaseInstallExecutionResponse(
                    contradictory, authorization));
        }
    }

    [Fact]
    public async Task SymlinkedBackupPathIsRejectedBeforeTimezoneMutation()
    {
        using var authority = CreateAuthority(out var signingKey);
        using (signingKey)
        {
            var plan = CreateTimezoneOnlyPlan("Europe/Berlin");
            var files = new FakeFileSystem([]);
            files.Files.Add("/usr/share/zoneinfo/Europe/Berlin");
            files.SymbolicLinks.Add("/var/lib/ochenstarik-server-monitor-manager/provisioning");
            var process = new FakeProcessRunner(
                [], new ProvisioningProcessResult(0, "UTC\n", ""));
            var executor = new TimezoneProvisioningExecutor(
                authority, "home", files, process, TimeProvider.System, "/var/lib/ochenstarik-server-monitor-manager/provisioning/rollback");

            var result = await executor.ExecuteAsync(
                CreateExecutionRequest(plan, SignGrant(authority, signingKey, plan)),
                TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Equal("backup.unsafe-path", result.Code);
            Assert.Empty(files.Writes);
            Assert.DoesNotContain(process.Calls,
                call => call.Arguments.Count > 0 && call.Arguments[0] == "set-timezone");
        }
    }

    [Fact]
    public async Task VerificationFailureAttemptsAndFactuallyVerifiesRollback()
    {
        using var authority = CreateAuthority(out var signingKey);
        using (signingKey)
        {
            var plan = CreateTimezoneOnlyPlan("Europe/Berlin");
            var events = new List<string>();
            var files = new FakeFileSystem(events);
            files.Files.Add("/usr/share/zoneinfo/Europe/Berlin");
            var process = new FakeProcessRunner(events,
                new(0, "UTC\n", ""),
                new(0, "", ""),
                new(0, "Etc/Unknown\n", ""),
                new(0, "", ""),
                new(0, "UTC\n", ""));
            var executor = new TimezoneProvisioningExecutor(
                authority, "home", files, process, TimeProvider.System, "/var/lib/ochenstarik-server-monitor-manager/provisioning/rollback");

            var result = await executor.ExecuteAsync(
                CreateExecutionRequest(plan, SignGrant(authority, signingKey, plan)),
                TestContext.Current.CancellationToken);

            Assert.False(result.Success);
            Assert.Equal("timezone.verification-failed", result.Code);
            Assert.True(result.RollbackAttempted);
            Assert.True(result.RollbackSucceeded);
            Assert.Equal(
                ["set-timezone Europe/Berlin", "set-timezone UTC"],
                process.Calls
                    .Where(call => call.Arguments.Count > 0 && call.Arguments[0] == "set-timezone")
                    .Select(call => string.Join(' ', call.Arguments)));
        }
    }

    [Fact]
    public async Task SilentClientDoesNotBlockASecondValidRequest()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var socketPath = Path.Combine(Path.GetTempPath(), $"smm-{Guid.NewGuid():N}.sock");
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        shutdown.CancelAfter(TimeSpan.FromSeconds(5));
        var server = new ProvisioningHelperServer(
            socketPath,
            expectedPeerUserId: GetEffectiveUserId(),
            connectionTimeout: TimeSpan.FromSeconds(1));
        var serverTask = server.RunAsync(shutdown.Token);
        using var silentClient = new Socket(
            AddressFamily.Unix,
            SocketType.Stream,
            ProtocolType.Unspecified);
        try
        {
            await WaitForSocketAsync(socketPath, shutdown.Token);
            await silentClient.ConnectAsync(
                new UnixDomainSocketEndPoint(socketPath),
                shutdown.Token);

            var response = await SendPreflightAsync(socketPath, shutdown.Token);

            Assert.True(response.Success);
            Assert.Equal("preflight.completed", response.Code);
        }
        finally
        {
            shutdown.Cancel();
            silentClient.Dispose();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        Assert.False(File.Exists(socketPath));
    }

    [Fact]
    public async Task SynchronousExecutionCannotBlockAcceptLoopAndIsCancelledOnShutdown()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var authority = CreateAuthority(out var signingKey);
        using (signingKey)
        using (var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken))
        {
            shutdown.CancelAfter(TimeSpan.FromSeconds(5));
            var plan = CreateTimezoneOnlyPlan("Europe/Berlin");
            var files = new FakeFileSystem([]);
            files.Files.Add("/usr/share/zoneinfo/Europe/Berlin");
            var process = new SynchronouslyBlockingProcessRunner();
            var executor = new TimezoneProvisioningExecutor(
                authority,
                "home",
                files,
                process,
                TimeProvider.System,
                "/var/lib/ochenstarik-server-monitor-manager/provisioning/rollback");
            var socketPath = Path.Combine(Path.GetTempPath(), $"smm-{Guid.NewGuid():N}.sock");
            var server = new ProvisioningHelperServer(
                socketPath,
                GetEffectiveUserId(),
                executor,
                connectionTimeout: TimeSpan.FromSeconds(5));
            var serverTask = server.RunAsync(shutdown.Token);
            Task<string?>? executionTask = null;
            try
            {
                await WaitForSocketAsync(socketPath, shutdown.Token);
                executionTask = SendRequestPayloadAsync(
                    socketPath,
                    CreateExecutionRequest(plan, SignGrant(authority, signingKey, plan)),
                    shutdown.Token);
                Assert.True(process.Started.Wait(
                    TimeSpan.FromSeconds(2),
                    TestContext.Current.CancellationToken));
                using var secondRequest = CancellationTokenSource.CreateLinkedTokenSource(
                    TestContext.Current.CancellationToken);
                secondRequest.CancelAfter(TimeSpan.FromSeconds(1));

                var response = await SendPreflightAsync(socketPath, secondRequest.Token);

                Assert.True(response.Success);
                Assert.Equal("preflight.completed", response.Code);
            }
            finally
            {
                shutdown.Cancel();
                await serverTask;
                if (executionTask is not null)
                {
                    try
                    {
                        await executionTask;
                    }
                    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                    {
                    }
                }
            }
            Assert.True(process.CancellationObserved);
            Assert.True(process.RecoveryObserved);
            Assert.False(File.Exists(socketPath));
        }
    }

    [Fact]
    public async Task UnexpectedPeerUserIdIsRejected()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var socketPath = Path.Combine(Path.GetTempPath(), $"smm-{Guid.NewGuid():N}.sock");
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        shutdown.CancelAfter(TimeSpan.FromSeconds(5));
        var currentUserId = GetEffectiveUserId();
        var expectedUserId = currentUserId == uint.MaxValue
            ? uint.MaxValue - 1
            : currentUserId + 1;
        var server = new ProvisioningHelperServer(socketPath, expectedUserId);
        var serverTask = server.RunAsync(shutdown.Token);
        try
        {
            await WaitForSocketAsync(socketPath, shutdown.Token);

            Assert.Null(await SendPreflightPayloadAsync(socketPath, shutdown.Token));
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task PartialConnectionIsClosedAfterTimeout()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var socketPath = Path.Combine(Path.GetTempPath(), $"smm-{Guid.NewGuid():N}.sock");
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        shutdown.CancelAfter(TimeSpan.FromSeconds(5));
        var server = new ProvisioningHelperServer(
            socketPath,
            GetEffectiveUserId(),
            connectionTimeout: TimeSpan.FromMilliseconds(100));
        var serverTask = server.RunAsync(shutdown.Token);
        try
        {
            await WaitForSocketAsync(socketPath, shutdown.Token);
            using var client = new Socket(
                AddressFamily.Unix,
                SocketType.Stream,
                ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), shutdown.Token);
            await client.SendAsync(new byte[] { (byte)'{' }, shutdown.Token);
            var buffer = new byte[1];

            var count = await client.ReceiveAsync(buffer, shutdown.Token);

            Assert.Equal(0, count);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task RequestRateLimitRejectsExcessConnections()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var socketPath = Path.Combine(Path.GetTempPath(), $"smm-{Guid.NewGuid():N}.sock");
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        shutdown.CancelAfter(TimeSpan.FromSeconds(5));
        var server = new ProvisioningHelperServer(
            socketPath,
            GetEffectiveUserId(),
            requestsPerMinute: 1);
        var serverTask = server.RunAsync(shutdown.Token);
        try
        {
            await WaitForSocketAsync(socketPath, shutdown.Token);

            Assert.NotNull(await SendPreflightPayloadAsync(socketPath, shutdown.Token));
            Assert.Null(await SendPreflightPayloadAsync(socketPath, shutdown.Token));
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    [Fact]
    public async Task RequestFramingIsBoundedAndListenerSurvivesInvalidInput()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var socketPath = Path.Combine(Path.GetTempPath(), $"smm-{Guid.NewGuid():N}.sock");
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        shutdown.CancelAfter(TimeSpan.FromSeconds(5));
        var server = new ProvisioningHelperServer(socketPath, GetEffectiveUserId());
        var serverTask = server.RunAsync(shutdown.Token);
        try
        {
            await WaitForSocketAsync(socketPath, shutdown.Token);

            var exactPayload = await SendPayloadAsync(
                socketPath,
                new string(' ', 16 * 1024) + "\n",
                shutdown.Token);
            var oversizedPayload = await SendPayloadAsync(
                socketPath,
                new string('x', 16 * 1024 + 1),
                shutdown.Token);
            var validResponse = await SendPreflightAsync(socketPath, shutdown.Token);

            var oversizedResponse = JsonSerializer.Deserialize(
                oversizedPayload!,
                SmmJsonContext.Default.ProvisioningHelperResponse)!;
            Assert.DoesNotContain("too large", exactPayload, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("request.invalid-size", oversizedResponse.Code);
            Assert.Equal("Invalid helper request size.", oversizedResponse.Message);
            Assert.True(validResponse.Success);
        }
        finally
        {
            shutdown.Cancel();
            await serverTask;
        }
    }

    private static async Task WaitForSocketAsync(string path, CancellationToken cancellationToken)
    {
        while (!File.Exists(path))
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    private static async Task<ProvisioningHelperResponse> SendPreflightAsync(
        string socketPath,
        CancellationToken cancellationToken)
    {
        var responsePayload = await SendPreflightPayloadAsync(socketPath, cancellationToken);
        return JsonSerializer.Deserialize(
            responsePayload!,
            SmmJsonContext.Default.ProvisioningHelperResponse)!;
    }

    private static async Task<string?> SendPreflightPayloadAsync(
        string socketPath,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse("{}");
        var request = new ProvisioningHelperRequest(
            "1", new string('e', 32), "preflight", 1,
            ProvisioningActionCatalog.PreflightModuleHash,
            document.RootElement.Clone());
        return await SendRequestPayloadAsync(socketPath, request, cancellationToken);
    }

    private static Task<string?> SendRequestPayloadAsync(
        string socketPath,
        ProvisioningHelperRequest request,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(
            request,
            SmmJsonContext.Default.ProvisioningHelperRequest) + "\n";
        return SendPayloadAsync(socketPath, payload, cancellationToken);
    }

    private static async Task<string?> SendPayloadAsync(
        string socketPath,
        string payload,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(
            AddressFamily.Unix,
            SocketType.Stream,
            ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
        try
        {
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(payload), cancellationToken);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadLineAsync(cancellationToken);
        }
        catch (IOException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    [DllImport("libc")]
    private static extern uint geteuid();

    private static uint GetEffectiveUserId() => geteuid();

    private static ProvisioningHelperRequest CreateExecutionRequest(
        SystemBaseInstallPlan plan,
        ProvisioningExecutionGrant grant)
    {
        using var document = JsonDocument.Parse("{}");
        return new ProvisioningHelperRequest(
            "1", new string('d', 32), "system.base-install", 1,
            ProvisioningActionCatalog.SystemBaseInstallModuleHash,
            document.RootElement.Clone(),
            new ProvisioningBaseInstallExecutionAuthorization("home", plan, grant));
    }

    private static SystemBaseInstallPlan CreateTimezoneOnlyPlan(string timezone)
        => new(timezone, "unchanged", false, false, [], "unchanged", null, 60, false, "never", []);

    private static X509Certificate2 CreateAuthority(out ECDsa key)
    {
        key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=SMM Test CA", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static ProvisioningExecutionGrant SignGrant(
        X509Certificate2 authority,
        ECDsa key,
        SystemBaseInstallPlan plan,
        DateTimeOffset? issuedAt = null,
        string nodeId = "home")
    {
        _ = authority;
        var now = issuedAt ?? DateTimeOffset.UtcNow;
        var grant = new ProvisioningExecutionGrant(
            "1", new string('d', 32), nodeId, "system.base-install", 1,
            ProvisioningExecutionGrantCodec.ComputePlanSha256(plan),
            now.ToUnixTimeSeconds(), now.AddMinutes(2).ToUnixTimeSeconds(),
            new string('e', 32), ProvisioningExecutionGrantCodec.SignatureAlgorithm, string.Empty);
        var signature = key.SignData(
            ProvisioningExecutionGrantCodec.CreateSigningPayload(grant),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return grant with { Signature = ProvisioningExecutionGrantCodec.EncodeBase64Url(signature) };
    }

    private sealed class FakeFileSystem(List<string> events) : IProvisioningFileSystem
    {
        public HashSet<string> Files { get; } = new(StringComparer.Ordinal);
        public HashSet<string> SymbolicLinks { get; } = new(StringComparer.Ordinal);
        public List<(string Path, string Content)> Writes { get; } = [];

        public bool FileExists(string path) => Files.Contains(path);

        public bool IsSymbolicLink(string path) => SymbolicLinks.Contains(path);

        public void CreateOwnerOnlyDirectory(string path)
            => events.Add($"mkdir:{path}");

        public void WriteOwnerOnlyFile(string path, string content)
        {
            if (Writes.Any(write => string.Equals(write.Path, path, StringComparison.Ordinal)))
            {
                throw new IOException("Owner-only file already exists.");
            }
            Writes.Add((path, content));
            events.Add("write-backup");
        }
    }

    private sealed class FakeProcessRunner(
        List<string> events,
        params ProvisioningProcessResult[] results) : IProvisioningProcessRunner
    {
        private readonly Queue<ProvisioningProcessResult> _results = new(results);
        public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<ProvisioningProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((fileName, arguments.ToArray()));
            events.Add($"process:{string.Join(' ', arguments)}");
            return Task.FromResult(_results.Count == 0
                ? throw new InvalidOperationException("Unexpected process call.")
                : _results.Dequeue());
        }
    }

    private sealed class SynchronouslyBlockingProcessRunner : IProvisioningProcessRunner
    {
        private int _calls;
        public ManualResetEventSlim Started { get; } = new(false);
        public bool CancellationObserved { get; private set; }
        public bool RecoveryObserved { get; private set; }

        public Task<ProvisioningProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                return Task.FromResult(new ProvisioningProcessResult(0, "UTC\n", ""));
            }
            if (call == 2)
            {
                Started.Set();
                cancellationToken.WaitHandle.WaitOne();
                CancellationObserved = cancellationToken.IsCancellationRequested;
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Cancellation was not observed.");
            }
            RecoveryObserved = true;
            return Task.FromResult(call switch
            {
                3 => new ProvisioningProcessResult(0, "Europe/Berlin\n", ""),
                4 => new ProvisioningProcessResult(0, "", ""),
                5 => new ProvisioningProcessResult(0, "UTC\n", ""),
                _ => throw new InvalidOperationException("Unexpected recovery process call.")
            });
        }
    }
}
