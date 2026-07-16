using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using ServerMonitorManager.Control;
using ServerMonitorManager.Core;
using Xunit;

namespace ServerMonitorManager.Control.Tests;

public sealed class ControlStoreTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"smm-tests-{Guid.NewGuid():N}");

    [Fact]
    public void DiagnosticsExportOmitsRawIdentitiesAndNormalizesStates()
    {
        const string endpoint = "root@secret.example.test:20202";
        const string node = "private-home-node:10.77.0.23";
        const string source = "confidential-ai-agent";
        const string target = "confidential-home";
        const string serverId = "private-local-server-id";

        var json = DiagnosticsExportService.CreateJson(
            [new DiagnosticServerInput(endpoint, true, true, false, 12.345)],
            [new DiagnosticNodeInput(node, "unexpected-private-state", 42)],
            [new DiagnosticLinkInput(source, target, "tcp", 22, "Partial", 7, 0)],
            [new DiagnosticMetricInput(serverId, DateTimeOffset.UtcNow, 1, 2, 3)],
            controlConfigured: true);

        Assert.DoesNotContain(endpoint, json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.example.test", json, StringComparison.Ordinal);
        Assert.DoesNotContain(node, json, StringComparison.Ordinal);
        Assert.DoesNotContain(source, json, StringComparison.Ordinal);
        Assert.DoesNotContain(target, json, StringComparison.Ordinal);
        Assert.DoesNotContain(serverId, json, StringComparison.Ordinal);
        Assert.Contains("endpoint_fingerprint", json, StringComparison.Ordinal);
        Assert.Contains("node_fingerprint", json, StringComparison.Ordinal);
        Assert.Contains("\"state\": \"unknown\"", json, StringComparison.Ordinal);
        Assert.Contains("\"state\": \"partial\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnrollmentTokenIsAtomicAndIdempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        var token = await store.CreateEnrollmentTokenAsync("home", TimeSpan.FromMinutes(10), cancellationToken);
        var idempotencyKey = Guid.NewGuid().ToString();
        var request = new EnrollmentRequest("home", token, "unused-in-store-test", idempotencyKey);
        var issued = new IssuedCertificate("certificate", "ca", "AA11", DateTimeOffset.UtcNow.AddYears(1));

        var first = await store.EnrollAsync(request, () => issued, cancellationToken);
        var retry = await store.EnrollAsync(request, () => throw new InvalidOperationException("must use cache"), cancellationToken);
        var replay = await store.EnrollAsync(
            request with { IdempotencyKey = Guid.NewGuid().ToString() },
            () => issued,
            cancellationToken);

        Assert.NotNull(first);
        Assert.Equal(first, retry);
        Assert.Null(replay);
        Assert.True(await store.IsCertificateForNodeAsync("AA11", "home", cancellationToken));
        Assert.False(await store.IsCertificateForNodeAsync("AA11", "other", cancellationToken));
    }

    [Fact]
    public async Task HeartbeatRetryDoesNotDuplicateMetricSample()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        var token = await store.CreateEnrollmentTokenAsync("home", TimeSpan.FromMinutes(10), cancellationToken);
        var issued = new IssuedCertificate("certificate", "ca", "BB22", DateTimeOffset.UtcNow.AddYears(1));
        await store.EnrollAsync(
            new EnrollmentRequest("home", token, "csr", Guid.NewGuid().ToString()),
            () => issued,
            cancellationToken);
        var heartbeat = new AgentHeartbeat(
            "home", "test", DateTimeOffset.UtcNow, 0.5, 1, 2, 3, 4, 5, 6, 7,
            Guid.NewGuid().ToString());

        var first = await store.RecordHeartbeatAsync(heartbeat, 30, cancellationToken);
        var retry = await store.RecordHeartbeatAsync(heartbeat, 30, cancellationToken);

        Assert.Equal(first.Response, retry.Response);
        Assert.True(first.RequiresReconciliation);
        Assert.True(retry.RequiresReconciliation);
        Assert.Equal(1, first.Response.Sequence);
        await store.CompleteAgentReconciliationAsync("home", cancellationToken);
        var completedRetry = await store.RecordHeartbeatAsync(heartbeat, 30, cancellationToken);
        Assert.False(completedRetry.RequiresReconciliation);
        await using (var connection = new SqliteConnection(
            $"Data Source={Path.Combine(_directory, "control.db")}"))
        {
            await connection.OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = "SELECT recorded_at FROM metric_samples LIMIT 1;";
            Assert.Equal(heartbeat.SentAt.ToString("O"), await command.ExecuteScalarAsync(cancellationToken));
        }
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => store.RecordHeartbeatAsync(
            heartbeat with { LoadOne = 0.9 },
            30,
            cancellationToken));
    }

    [Fact]
    public async Task ReconnectReappliesOnlyLatestDisabledPolicy()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "ai-agent", "AB12", cancellationToken);
        await EnrollAgentAsync(store, "home", "CD34", cancellationToken);
        var broker = new ControlEventBroker();
        var applier = new CountingPolicyApplier();
        var service = new LinkService(store, applier, broker);

        var first = await service.CreateAsync(
            new LinkPolicyCreateRequest(
                "ai-agent", "home", "tcp", 22, 60, "first", Guid.NewGuid().ToString()),
            "windows-pc",
            cancellationToken);
        await service.DisableAsync(
            first.Id,
            new LinkPolicyDisableRequest(Guid.NewGuid().ToString()),
            "windows-pc",
            cancellationToken);
        _ = await service.CreateAsync(
            new LinkPolicyCreateRequest(
                "ai-agent", "home", "tcp", 22, 60, "replacement", Guid.NewGuid().ToString()),
            "windows-pc",
            cancellationToken);

        var disconnectsBeforeStaleRequest = applier.DisconnectCalls;
        var stale = await service.DisableAsync(
            first.Id,
            new LinkPolicyDisableRequest(Guid.NewGuid().ToString()),
            "windows-pc",
            cancellationToken);
        Assert.Equal("Disabled", stale?.DesiredState);
        Assert.Equal(disconnectsBeforeStaleRequest, applier.DisconnectCalls);

        var activeResult = await service.ReconcileDisabledLinksForNodeAsync("home", cancellationToken);
        Assert.Equal(new LinkReconciliationResult(0, 0), activeResult);

        var latest = (await store.ListEffectiveLinksForNodeAsync("home", cancellationToken)).Single();
        await service.DisableAsync(
            latest.Id,
            new LinkPolicyDisableRequest(Guid.NewGuid().ToString()),
            "windows-pc",
            cancellationToken);
        var beforeReconnect = applier.DisconnectCalls;

        var disabledResult = await service.ReconcileDisabledLinksForNodeAsync("home", cancellationToken);

        Assert.Equal(new LinkReconciliationResult(1, 0), disabledResult);
        Assert.Equal(beforeReconnect + 1, applier.DisconnectCalls);
        var persisted = (await store.ListEffectiveLinksForNodeAsync("home", cancellationToken)).Single();
        Assert.Equal("Disabled", persisted.DesiredState);
        Assert.Equal("Disabled", persisted.ActualState);
    }

    [Fact]
    public async Task FailedReconnectRemainsPendingUntilFirewallSucceeds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "ai-agent", "EF56", cancellationToken);
        await EnrollAgentAsync(store, "home", "7890", cancellationToken);
        var applier = new CountingPolicyApplier();
        var service = new LinkService(store, applier, new ControlEventBroker());
        var active = await service.CreateAsync(
            new LinkPolicyCreateRequest(
                "ai-agent", "home", "tcp", 22, 60, "test", Guid.NewGuid().ToString()),
            "windows-pc",
            cancellationToken);
        await service.DisableAsync(
            active.Id,
            new LinkPolicyDisableRequest(Guid.NewGuid().ToString()),
            "windows-pc",
            cancellationToken);

        var heartbeat = new AgentHeartbeat(
            "home", "test", DateTimeOffset.UtcNow, 0.5, 1, 2, 3, 4, 5, 6, 7,
            Guid.NewGuid().ToString());
        var first = await store.RecordHeartbeatAsync(heartbeat, 30, cancellationToken);
        Assert.True(first.RequiresReconciliation);

        applier.FailDisconnect = true;
        var failed = await service.ReconcileDisabledLinksForNodeAsync("home", cancellationToken);
        Assert.Equal(new LinkReconciliationResult(1, 1), failed);
        var retry = await store.RecordHeartbeatAsync(
            heartbeat with
            {
                SentAt = DateTimeOffset.UtcNow,
                IdempotencyKey = Guid.NewGuid().ToString()
            },
            30,
            cancellationToken);
        Assert.True(retry.RequiresReconciliation);

        applier.FailDisconnect = false;
        var succeeded = await service.ReconcileDisabledLinksForNodeAsync("home", cancellationToken);
        Assert.Equal(new LinkReconciliationResult(1, 0), succeeded);
        await store.CompleteAgentReconciliationAsync("home", cancellationToken);
        var completed = await store.RecordHeartbeatAsync(
            heartbeat with
            {
                SentAt = DateTimeOffset.UtcNow,
                IdempotencyKey = Guid.NewGuid().ToString()
            },
            30,
            cancellationToken);
        Assert.False(completed.RequiresReconciliation);
    }

    [Fact]
    public async Task DeviceCertificateReceivesOperatorIdentity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        var token = await store.CreateDeviceEnrollmentTokenAsync(
            "windows-pc", TimeSpan.FromMinutes(10), cancellationToken);
        var issued = new IssuedCertificate("certificate", "ca", "CC33", DateTimeOffset.UtcNow.AddYears(1));
        var request = new DeviceEnrollmentRequest(
            "windows-pc", token, "csr", Guid.NewGuid().ToString());

        var first = await store.EnrollDeviceAsync(request, () => issued, cancellationToken);
        var retry = await store.EnrollDeviceAsync(
            request,
            () => throw new InvalidOperationException("must use cache"),
            cancellationToken);
        var identity = await store.ResolveIdentityAsync("CC33", cancellationToken);

        Assert.NotNull(first);
        Assert.Equal(first, retry);
        Assert.Equal(new ControlIdentity("windows-pc", "Operator"), identity);
        Assert.False(await store.IsCertificateForNodeAsync("CC33", "home", cancellationToken));
    }

    [Fact]
    public async Task LinkDesiredStateIsPersistedBeforeActualStateChanges()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "ai-agent", "DD44", cancellationToken);
        await EnrollAgentAsync(store, "home", "EE55", cancellationToken);
        var request = new LinkPolicyCreateRequest(
            "ai-agent", "home", "tcp", 22, 120, "development", Guid.NewGuid().ToString());

        var connectingMutation = await store.CreateLinkMutationAsync(request, "windows-pc", cancellationToken);
        var connecting = connectingMutation.Link;
        var active = await store.SetLinkActualStateAsync(
            connecting.Id, "Active", null, "windows-pc", cancellationToken);
        var disconnectingMutation = await store.BeginDisableLinkMutationAsync(
            connecting.Id,
            new LinkPolicyDisableRequest(Guid.NewGuid().ToString()),
            "windows-pc",
            cancellationToken);
        var disconnecting = disconnectingMutation?.Link;
        var disabled = await store.SetLinkActualStateAsync(
            connecting.Id, "Disabled", null, "windows-pc", cancellationToken);
        var links = await store.ListLinksAsync(cancellationToken);

        Assert.Equal("Active", connecting.DesiredState);
        Assert.Equal("Connecting", connecting.ActualState);
        Assert.Equal("Active", active?.ActualState);
        Assert.Equal("Disabled", disconnecting?.DesiredState);
        Assert.Equal("Disconnecting", disconnecting?.ActualState);
        Assert.Equal("Disabled", disabled?.ActualState);
        Assert.Single(links);
        Assert.Equal(disabled, links[0]);
    }

    [Fact]
    public async Task LinkServiceAppliesPersistedStatesAndPublishesEvents()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "ai-agent", "FF66", cancellationToken);
        await EnrollAgentAsync(store, "home", "0011", cancellationToken);
        var broker = new ControlEventBroker();
        using var subscription = broker.Subscribe();
        var applier = new CheckingPolicyApplier(store);
        var service = new LinkService(store, applier, broker);
        var createRequest = new LinkPolicyCreateRequest(
            "ai-agent", "home", "tcp", 22, 30, "test", Guid.NewGuid().ToString());

        var active = await service.CreateAsync(
            createRequest,
            "windows-pc",
            cancellationToken);
        var createReplay = await service.CreateAsync(createRequest, "windows-pc", cancellationToken);
        var disableRequest = new LinkPolicyDisableRequest(Guid.NewGuid().ToString());
        var disabled = await service.DisableAsync(
            active.Id,
            disableRequest,
            "windows-pc",
            cancellationToken);
        var disableReplay = await service.DisableAsync(
            active.Id, disableRequest, "windows-pc", cancellationToken);
        var eventTypes = new List<string>();
        while (subscription.Reader.TryRead(out var controlEvent))
        {
            eventTypes.Add(controlEvent.Type);
        }

        Assert.Equal("Active", active.ActualState);
        Assert.Equal(active, createReplay);
        Assert.Equal("Disabled", disabled?.DesiredState);
        Assert.Equal("Disabled", disabled?.ActualState);
        Assert.Equal(disabled, disableReplay);
        Assert.Equal(1, applier.ConnectCalls);
        Assert.Equal(1, applier.DisconnectCalls);
        Assert.Equal(
            ["link.connecting", "link.active", "link.disconnecting", "link.disabled"],
            eventTypes);
    }

    [Fact]
    public async Task AgentReenrollmentRevokesCertificateAndDisablesLinksBeforeIssuingToken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "ai-agent", "AA22", cancellationToken);
        await EnrollAgentAsync(store, "home", "BB33", cancellationToken);
        var broker = new ControlEventBroker();
        var applier = new CheckingPolicyApplier(store);
        var links = new LinkService(store, applier, broker);
        await links.CreateAsync(
            new LinkPolicyCreateRequest(
                "ai-agent", "home", "tcp", 22, 60, "development", Guid.NewGuid().ToString()),
            "windows-pc",
            cancellationToken);
        var lifecycle = new CertificateLifecycleService(store, applier, broker);
        var request = new CertificateReenrollmentRequest("rotate compromised key", Guid.NewGuid().ToString());

        var ticket = await lifecycle.ReenrollAgentAsync(
            "ai-agent", request, "windows-pc", cancellationToken);
        var replay = await lifecycle.ReenrollAgentAsync(
            "ai-agent", request, "windows-pc", cancellationToken);
        var disabledLink = Assert.Single(await store.ListLinksAsync(cancellationToken));

        Assert.NotNull(ticket);
        Assert.Equal(ticket, replay);
        Assert.Equal("Agent", ticket.EntityType);
        Assert.Equal("ai-agent", ticket.EntityId);
        Assert.Equal(1, ticket.DisabledLinks);
        Assert.True(ticket.ExpiresAt > DateTimeOffset.UtcNow);
        Assert.False(await store.IsCertificateForNodeAsync("AA22", "ai-agent", cancellationToken));
        Assert.Equal("Disabled", disabledLink.DesiredState);
        Assert.Equal("Disabled", disabledLink.ActualState);
        Assert.Equal(1, applier.DisconnectCalls);

        var issued = new IssuedCertificate(
            "new-certificate", "ca", "CC44", DateTimeOffset.UtcNow.AddYears(1));
        var enrolled = await store.EnrollAsync(
            new EnrollmentRequest(
                "ai-agent", ticket.Token, "new-csr", Guid.NewGuid().ToString()),
            () => issued,
            cancellationToken);
        Assert.NotNull(enrolled);
        Assert.True(await store.IsCertificateForNodeAsync("CC44", "ai-agent", cancellationToken));
        Assert.False(await store.IsCertificateForNodeAsync("AA22", "ai-agent", cancellationToken));
    }

    [Fact]
    public async Task DeviceReenrollmentRevokesOldOperatorAndIsIdempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        var initialToken = await store.CreateDeviceEnrollmentTokenAsync(
            "windows-pc", TimeSpan.FromMinutes(10), cancellationToken);
        await store.EnrollDeviceAsync(
            new DeviceEnrollmentRequest(
                "windows-pc", initialToken, "old-csr", Guid.NewGuid().ToString()),
            () => new IssuedCertificate(
                "old-certificate", "ca", "DD55", DateTimeOffset.UtcNow.AddYears(1)),
            cancellationToken);
        var request = new CertificateReenrollmentRequest("scheduled rotation", Guid.NewGuid().ToString());

        var ticket = await store.BeginDeviceReenrollmentAsync(
            "windows-pc", request, "windows-pc", TimeSpan.FromMinutes(10), cancellationToken);
        var replay = await store.BeginDeviceReenrollmentAsync(
            "windows-pc", request, "windows-pc", TimeSpan.FromMinutes(10), cancellationToken);

        Assert.NotNull(ticket);
        Assert.Equal(ticket, replay);
        Assert.Null(await store.ResolveIdentityAsync("DD55", cancellationToken));
        var enrolled = await store.EnrollDeviceAsync(
            new DeviceEnrollmentRequest(
                "windows-pc", ticket.Token, "new-csr", Guid.NewGuid().ToString()),
            () => new IssuedCertificate(
                "new-certificate", "ca", "EE66", DateTimeOffset.UtcNow.AddYears(1)),
            cancellationToken);
        Assert.NotNull(enrolled);
        Assert.Equal(
            new ControlIdentity("windows-pc", "Operator"),
            await store.ResolveIdentityAsync("EE66", cancellationToken));
        Assert.Null(await store.ResolveIdentityAsync("DD55", cancellationToken));
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
        return ValueTask.CompletedTask;
    }

    private ControlStore CreateStore()
    {
        Directory.CreateDirectory(_directory);
        return new ControlStore(Options.Create(new ControlOptions
        {
            DatabasePath = Path.Combine(_directory, "control.db"),
            CertificateAuthorityPath = Path.Combine(_directory, "unused.pfx")
        }));
    }

    private static async Task EnrollAgentAsync(
        ControlStore store,
        string nodeId,
        string thumbprint,
        CancellationToken cancellationToken)
    {
        var token = await store.CreateEnrollmentTokenAsync(nodeId, TimeSpan.FromMinutes(10), cancellationToken);
        var issued = new IssuedCertificate(
            "certificate", "ca", thumbprint, DateTimeOffset.UtcNow.AddYears(1));
        var result = await store.EnrollAsync(
            new EnrollmentRequest(nodeId, token, "csr", Guid.NewGuid().ToString()),
            () => issued,
            cancellationToken);
        Assert.NotNull(result);
    }

    private sealed class CheckingPolicyApplier(ControlStore store) : ILinkPolicyApplier
    {
        public int ConnectCalls { get; private set; }
        public int DisconnectCalls { get; private set; }

        public async Task ApplyConnectAsync(LinkPolicy link, CancellationToken cancellationToken)
        {
            ConnectCalls++;
            var persisted = Assert.Single(await store.ListLinksAsync(cancellationToken));
            Assert.Equal(link.Id, persisted.Id);
            Assert.Equal("Active", persisted.DesiredState);
            Assert.Equal("Connecting", persisted.ActualState);
        }

        public async Task ApplyDisconnectAsync(LinkPolicy link, CancellationToken cancellationToken)
        {
            DisconnectCalls++;
            var persisted = Assert.Single(await store.ListLinksAsync(cancellationToken));
            Assert.Equal(link.Id, persisted.Id);
            Assert.Equal("Disabled", persisted.DesiredState);
            Assert.Equal("Disconnecting", persisted.ActualState);
        }
    }

    private sealed class CountingPolicyApplier : ILinkPolicyApplier
    {
        public int ConnectCalls { get; private set; }
        public int DisconnectCalls { get; private set; }
        public bool FailDisconnect { get; set; }

        public Task ApplyConnectAsync(LinkPolicy link, CancellationToken cancellationToken)
        {
            ConnectCalls++;
            return Task.CompletedTask;
        }

        public Task ApplyDisconnectAsync(LinkPolicy link, CancellationToken cancellationToken)
        {
            DisconnectCalls++;
            if (FailDisconnect)
            {
                throw new InvalidOperationException("simulated firewall failure");
            }
            return Task.CompletedTask;
        }
    }
}
