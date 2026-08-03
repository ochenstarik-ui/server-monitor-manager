using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ServerMonitorManager.Control;
using ServerMonitorManager.Core;
using Xunit;

namespace ServerMonitorManager.Control.Tests;

public sealed class LinkReconciliationTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"smm-link-reconciliation-{Guid.NewGuid():N}");

    [Fact]
    public async Task AllPolicyPassReappliesErasedActiveRulesWithoutHeartbeat()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "source", "AA11", cancellationToken);
        await EnrollAgentAsync(store, "target-one", "BB22", cancellationToken);
        await EnrollAgentAsync(store, "target-two", "CC33", cancellationToken);
        var applier = new RecordingPolicyApplier();
        var service = new LinkService(store, applier, new ControlEventBroker());
        var first = await service.CreateAsync(CreateRequest("target-one"), "operator", cancellationToken);
        var second = await service.CreateAsync(CreateRequest("target-two"), "operator", cancellationToken);
        applier.EraseRules();
        var mutationsBefore = applier.ConnectCalls;

        var result = await service.ReconcileAllAsync(cancellationToken);

        Assert.Equal(2, result.Examined);
        Assert.Equal(0, result.Failed);
        Assert.Equal(mutationsBefore + 2, applier.ConnectCalls);
        Assert.All(await store.ListEffectiveLinksAsync(cancellationToken),
            link => Assert.Equal("Active", link.ActualState));
        Assert.True(applier.IsConnected(first));
        Assert.True(applier.IsConnected(second));
    }

    [Fact]
    public async Task SecondUnchangedPassDoesNotMutateFirewall()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "source", "A111", cancellationToken);
        await EnrollAgentAsync(store, "target-one", "B222", cancellationToken);
        var applier = new RecordingPolicyApplier();
        var service = new LinkService(store, applier, new ControlEventBroker());
        await service.CreateAsync(CreateRequest("target-one"), "operator", cancellationToken);

        await service.ReconcileAllAsync(cancellationToken);
        var connects = applier.ConnectCalls;
        var disconnects = applier.DisconnectCalls;
        await service.ReconcileAllAsync(cancellationToken);

        Assert.Equal(connects, applier.ConnectCalls);
        Assert.Equal(disconnects, applier.DisconnectCalls);
    }

    [Fact]
    public async Task AllPolicyPassRemovesOrphanRuleForDesiredDisabledLink()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "source", "DD44", cancellationToken);
        await EnrollAgentAsync(store, "target-one", "EE55", cancellationToken);
        var broker = new ControlEventBroker();
        using var subscription = broker.Subscribe();
        var applier = new RecordingPolicyApplier();
        var service = new LinkService(store, applier, broker);
        var active = await service.CreateAsync(CreateRequest("target-one"), "operator", cancellationToken);
        await store.BeginDisableLinkMutationAsync(
            active.Id,
            new LinkPolicyDisableRequest(Guid.NewGuid().ToString()),
            "operator",
            cancellationToken);

        await service.ReconcileAllAsync(cancellationToken);

        Assert.Equal(1, applier.DisconnectCalls);
        Assert.Equal("Disabled", (await store.GetLinkAsync(active.Id, cancellationToken))!.ActualState);
        var eventTypes = new List<string>();
        while (subscription.Reader.TryRead(out var controlEvent))
        {
            eventTypes.Add(controlEvent.Type);
        }
        Assert.Contains("link.orphan-removed", eventTypes);
    }

    [Fact]
    public async Task FirewallUnavailableAbortsWithoutMutationsAndPublishesOneSharedEvent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "source", "FF66", cancellationToken);
        await EnrollAgentAsync(store, "target-one", "0011", cancellationToken);
        await EnrollAgentAsync(store, "target-two", "0022", cancellationToken);
        var broker = new ControlEventBroker();
        using var subscription = broker.Subscribe();
        var applier = new RecordingPolicyApplier();
        var service = new LinkService(store, applier, broker);
        await service.CreateAsync(CreateRequest("target-one"), "operator", cancellationToken);
        await service.CreateAsync(CreateRequest("target-two"), "operator", cancellationToken);
        while (subscription.Reader.TryRead(out _)) { }
        var connectBefore = applier.ConnectCalls;
        var disconnectBefore = applier.DisconnectCalls;
        applier.FirewallUnavailable = true;

        var result = await service.ReconcileAllAsync(cancellationToken);

        Assert.True(result.FirewallUnavailable);
        Assert.Equal(connectBefore, applier.ConnectCalls);
        Assert.Equal(disconnectBefore, applier.DisconnectCalls);
        Assert.All(await store.ListEffectiveLinksAsync(cancellationToken), link =>
        {
            Assert.Equal("Partial", link.ActualState);
            Assert.Equal(LinkService.FirewallUnavailableCode, link.LastError);
        });
        var eventTypes = new List<string>();
        while (subscription.Reader.TryRead(out var controlEvent))
        {
            eventTypes.Add(controlEvent.Type);
        }
        Assert.Equal([LinkService.FirewallUnavailableCode], eventTypes);

        applier.FirewallUnavailable = false;
        Assert.False((await service.ReconcileAllAsync(cancellationToken)).FirewallUnavailable);
        eventTypes.Clear();
        while (subscription.Reader.TryRead(out var controlEvent))
        {
            eventTypes.Add(controlEvent.Type);
        }
        Assert.Single(eventTypes, eventType => eventType == LinkService.FirewallAvailableCode);
    }

    [Fact]
    public async Task MarkerCreatedAfterNormalPassTriggersPromptAdditionalPass()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "source", "A333", cancellationToken);
        await EnrollAgentAsync(store, "target-one", "B444", cancellationToken);
        var applier = new RecordingPolicyApplier();
        var links = new LinkService(store, applier, new ControlEventBroker());
        await links.CreateAsync(CreateRequest("target-one"), "operator", cancellationToken);
        var background = CreateBackgroundService(
            links,
            applier,
            new TestTimeProvider(DateTimeOffset.Parse("2026-08-03T12:00:00Z")));

        Assert.NotNull(await background.RunOnceAsync(cancellationToken));
        Assert.Equal(0, applier.CompleteReconciliationCalls);
        applier.ReconciliationRequest = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";

        Assert.NotNull(await background.RunOnceAsync(cancellationToken));
        Assert.Equal(2, applier.ReconciliationStatusCalls);
        Assert.Equal(1, applier.CompleteReconciliationCalls);
        Assert.Null(applier.ReconciliationRequest);
        Assert.Null(await background.RunOnceAsync(cancellationToken));
    }

    [Fact]
    public async Task CompletingObservedGenerationDoesNotConsumeNewerRequest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "source", "A355", cancellationToken);
        await EnrollAgentAsync(store, "target-one", "B466", cancellationToken);
        var generationA = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        var generationB = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
        var applier = new RecordingPolicyApplier { ReconciliationRequest = generationA };
        var links = new LinkService(store, applier, new ControlEventBroker());
        await links.CreateAsync(CreateRequest("target-one"), "operator", cancellationToken);
        applier.ReplacementRequestOnNextProbe = generationB;
        var background = CreateBackgroundService(
            links,
            applier,
            new TestTimeProvider(DateTimeOffset.Parse("2026-08-03T12:00:00Z")));

        Assert.NotNull(await background.RunOnceAsync(cancellationToken));
        Assert.Equal([generationA], applier.CompletedGenerations);
        Assert.Equal(generationB, applier.ReconciliationRequest);

        Assert.NotNull(await background.RunOnceAsync(cancellationToken));
        Assert.Equal([generationA, generationB], applier.CompletedGenerations);
        Assert.Null(applier.ReconciliationRequest);
    }

    [Fact]
    public async Task BackgroundPassRetainsMarkerWhenFirewallIsUnavailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "source", "A555", cancellationToken);
        await EnrollAgentAsync(store, "target-one", "B666", cancellationToken);
        var applier = new RecordingPolicyApplier
        {
            ReconciliationRequest = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
        };
        var links = new LinkService(store, applier, new ControlEventBroker());
        await links.CreateAsync(CreateRequest("target-one"), "operator", cancellationToken);
        applier.FirewallUnavailable = true;
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-08-03T12:00:00Z"));
        var background = CreateBackgroundService(links, applier, time);

        var result = await background.RunOnceAsync(cancellationToken);

        Assert.NotNull(result);
        Assert.True(result.FirewallUnavailable);
        Assert.Equal(0, applier.CompleteReconciliationCalls);
        Assert.NotNull(applier.ReconciliationRequest);
        applier.FirewallUnavailable = false;
        Assert.Null(await background.RunOnceAsync(cancellationToken));
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.NotNull(await background.RunOnceAsync(cancellationToken));
        Assert.Null(applier.ReconciliationRequest);
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

    private static LinkPolicyCreateRequest CreateRequest(string target)
        => new("source", target, "tcp", 22, 0, "test", Guid.NewGuid().ToString());

    private static LinkReconciliationBackgroundService CreateBackgroundService(
        LinkService links,
        ILinkPolicyApplier applier,
        TimeProvider timeProvider)
        => new(
            links,
            applier,
            Options.Create(new ControlOptions { LinkReconciliationSeconds = 30 }),
            timeProvider,
            NullLogger<LinkReconciliationBackgroundService>.Instance);

    private static async Task EnrollAgentAsync(
        ControlStore store,
        string nodeId,
        string thumbprint,
        CancellationToken cancellationToken)
    {
        var token = await store.CreateEnrollmentTokenAsync(nodeId, TimeSpan.FromMinutes(10), cancellationToken);
        Assert.NotNull(await store.EnrollAsync(
            new EnrollmentRequest(nodeId, token, "csr", Guid.NewGuid().ToString()),
            () => new IssuedCertificate("certificate", "ca", thumbprint, DateTimeOffset.UtcNow.AddYears(1)),
            cancellationToken));
    }

    private sealed class RecordingPolicyApplier : ILinkPolicyApplier
    {
        private readonly HashSet<string> _connected = [];
        public int ConnectCalls { get; private set; }
        public int DisconnectCalls { get; private set; }
        public bool FirewallUnavailable { get; set; }
        public string? ReconciliationRequest { get; set; }
        public string? ReplacementRequestOnNextProbe { get; set; }
        public int ReconciliationStatusCalls { get; private set; }
        public int CompleteReconciliationCalls { get; private set; }
        public List<string> CompletedGenerations { get; } = [];

        public void EraseRules() => _connected.Clear();
        public bool IsConnected(LinkPolicy link) => _connected.Contains(link.Id);

        public Task ApplyConnectAsync(LinkPolicy link, CancellationToken cancellationToken)
        {
            ConnectCalls++;
            _connected.Add(link.Id);
            return Task.CompletedTask;
        }

        public Task ApplyDisconnectAsync(LinkPolicy link, CancellationToken cancellationToken)
        {
            DisconnectCalls++;
            _connected.Remove(link.Id);
            return Task.CompletedTask;
        }

        public Task<bool> IsConnectedAsync(LinkPolicy link, CancellationToken cancellationToken)
        {
            if (ReplacementRequestOnNextProbe is not null)
            {
                ReconciliationRequest = ReplacementRequestOnNextProbe;
                ReplacementRequestOnNextProbe = null;
            }
            return FirewallUnavailable
                ? Task.FromException<bool>(new MeshFirewallUnavailableException(LinkService.FirewallUnavailableCode))
                : Task.FromResult(IsConnected(link));
        }

        public Task<string?> GetReconciliationRequestAsync(CancellationToken cancellationToken)
        {
            ReconciliationStatusCalls++;
            return Task.FromResult(ReconciliationRequest);
        }

        public Task CompleteReconciliationAsync(string generation, CancellationToken cancellationToken)
        {
            CompleteReconciliationCalls++;
            CompletedGenerations.Add(generation);
            if (ReconciliationRequest == generation)
            {
                ReconciliationRequest = null;
            }
            return Task.CompletedTask;
        }
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
}
