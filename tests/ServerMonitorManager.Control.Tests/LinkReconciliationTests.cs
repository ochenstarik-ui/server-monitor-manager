using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using ServerMonitorManager.Control;
using ServerMonitorManager.Core;
using Xunit;

namespace ServerMonitorManager.Control.Tests;

public sealed class LinkReconciliationTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"smm-link-reconciliation-{Guid.NewGuid():N}");

    [Fact]
    public void ReconciliationResultsRejectNonExhaustiveClassificationsWithIds()
    {
        var full = Assert.Throws<InvalidOperationException>(() =>
            new LinkFullReconciliationResult(6, 2, 2, 1, false, ["failed"], ["deferred"]));
        Assert.Contains("examined=6", full.Message, StringComparison.Ordinal);
        Assert.Contains("failed IDs=[failed]", full.Message, StringComparison.Ordinal);

        var node = Assert.Throws<InvalidOperationException>(() =>
            new LinkReconciliationResult(3, 1, 0, 1, [], ["deferred"]));
        Assert.Contains("examined=3", node.Message, StringComparison.Ordinal);
        Assert.Contains("deferred IDs=[deferred]", node.Message, StringComparison.Ordinal);
    }

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
        var privilegedBefore = applier.PrivilegedCalls;
        await service.ReconcileAllAsync(cancellationToken);

        Assert.Equal(connects, applier.ConnectCalls);
        Assert.Equal(disconnects, applier.DisconnectCalls);
        Assert.Equal(1, applier.PrivilegedCalls - privilegedBefore);
    }

    [Fact]
    public async Task FullPassWithMultipleMutationsUsesOneInitialAndOneFinalList()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "source", "A011", cancellationToken);
        var applier = new RecordingPolicyApplier();
        var service = new LinkService(store, applier, new ControlEventBroker());
        for (var index = 1; index <= 4; index++)
        {
            var target = $"target-{index}";
            await EnrollAgentAsync(store, target, $"B0{index}2", cancellationToken);
            await service.CreateAsync(CreateRequest(target), "operator", cancellationToken);
        }
        applier.EraseRules();
        var listsBefore = applier.ListCalls;
        var connectsBefore = applier.ConnectCalls;

        var result = await service.ReconcileAllAsync(cancellationToken);

        Assert.Equal((4, 4, 0, 0),
            (result.Examined, result.Converged, result.Failed, result.Deferred));
        Assert.Equal(2, applier.ListCalls - listsBefore);
        Assert.Equal(4, applier.ConnectCalls - connectsBefore);
    }

    [Fact]
    public async Task BatchFinalizationDoesNotFinalizeOrPublishStalePolicyVersion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "source", "A021", cancellationToken);
        await EnrollAgentAsync(store, "target-one", "B032", cancellationToken);
        var broker = new ControlEventBroker();
        using var subscription = broker.Subscribe();
        var applier = new RecordingPolicyApplier();
        var service = new LinkService(store, applier, broker);
        var link = await service.CreateAsync(CreateRequest("target-one"), "operator", cancellationToken);
        applier.EraseRules();
        while (subscription.Reader.TryRead(out _)) { }
        var listsBefore = applier.ListCalls;
        applier.PauseOnListCall = listsBefore + 2;

        var pass = service.ReconcileAllAsync(cancellationToken);
        await applier.PausedListObserved.Task.WaitAsync(cancellationToken);
        var disabled = await store.BeginDisableLinkMutationAsync(
            link.Id,
            new LinkPolicyDisableRequest(Guid.NewGuid().ToString()),
            "operator",
            cancellationToken);
        Assert.NotNull(disabled);
        Assert.True(disabled.Link.Version > link.Version);
        applier.ReleasePausedList.TrySetResult();

        var result = await pass;

        Assert.Equal((1, 0, 1, 0),
            (result.Examined, result.Converged, result.Failed, result.Deferred));
        Assert.Equal(2, applier.ListCalls - listsBefore);
        var persisted = Assert.IsType<LinkPolicy>(await store.GetLinkAsync(link.Id, cancellationToken));
        Assert.Equal("Disabled", persisted.DesiredState);
        Assert.Equal("Disconnecting", persisted.ActualState);
        var eventTypes = new List<string>();
        while (subscription.Reader.TryRead(out var controlEvent))
        {
            eventTypes.Add(controlEvent.Type);
        }
        Assert.DoesNotContain("link.active", eventTypes);
        Assert.DoesNotContain("link.reapplied", eventTypes);
    }

    [Fact]
    public async Task FactualOrphanWithoutDatabasePolicyIsRemovedAndAudited()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        var broker = new ControlEventBroker();
        using var subscription = broker.Subscribe();
        var applier = new RecordingPolicyApplier();
        var orphan = new LinkRule("source", "missing", "tcp", 22);
        applier.InjectRule(orphan);
        var service = new LinkService(store, applier, broker);

        var result = await service.ReconcileAllAsync(cancellationToken);

        Assert.Equal((1, 1, 0), (result.Examined, result.Converged, result.Failed));
        Assert.Equal(1, applier.DisconnectCalls);
        Assert.DoesNotContain(orphan, await applier.ListRulesAsync(cancellationToken));
        var eventTypes = new List<string>();
        while (subscription.Reader.TryRead(out var controlEvent))
        {
            eventTypes.Add(controlEvent.Type);
        }
        Assert.Contains("link.orphan-removed", eventTypes);

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "control.db")}");
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT details_json FROM audit
            WHERE action = 'link.orphan-removed'
            ORDER BY sequence DESC LIMIT 1;
            """;
        var payload = Assert.IsType<string>(await command.ExecuteScalarAsync(cancellationToken));
        using var document = JsonDocument.Parse(payload);
        Assert.Equal("source", document.RootElement.GetProperty("sourceNodeId").GetString());
        Assert.Equal("missing", document.RootElement.GetProperty("targetNodeId").GetString());
        Assert.Equal("tcp", document.RootElement.GetProperty("protocol").GetString());
        Assert.Equal(22, document.RootElement.GetProperty("port").GetInt32());
    }

    [Fact]
    public async Task DatabaseLessOrphanIsFailedWhenDisconnectReportsSuccessButRuleRemains()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        var rule = new LinkRule("source", "missing", "tcp", 22);
        var applier = new RecordingPolicyApplier { DisconnectLeavesRules = true };
        applier.InjectRule(rule);
        var service = new LinkService(store, applier, new ControlEventBroker());

        var result = await service.ReconcileAllAsync(cancellationToken);

        Assert.Equal((1, 0, 1), (result.Examined, result.Converged, result.Failed));
        Assert.Contains(rule, await applier.ListRulesAsync(cancellationToken));
        Assert.Contains(result.FailedPolicyIds, id => id.StartsWith("orphan:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PersistedDisabledCleanupDoesNotProbeMissingNodeStatus()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "source", "D311", cancellationToken);
        await EnrollAgentAsync(store, "target-one", "D322", cancellationToken);
        var applier = new RecordingPolicyApplier();
        var service = new LinkService(store, applier, new ControlEventBroker());
        var link = await service.CreateAsync(CreateRequest("target-one"), "operator", cancellationToken);
        await service.DisableAsync(
            link.Id, new LinkPolicyDisableRequest(Guid.NewGuid().ToString()), "operator", cancellationToken);
        applier.InjectRule(new LinkRule("source", "target-one", "tcp", 22));
        applier.ThrowNodeNotActivatedOnStatus = true;

        var result = await service.ReconcileAllAsync(cancellationToken);

        Assert.Equal((1, 1, 0), (result.Examined, result.Converged, result.Failed));
        Assert.Equal("Disabled", (await store.GetLinkAsync(link.Id, cancellationToken))!.ActualState);
    }

    [Fact]
    public async Task ExactPostMutationCountRejectsDuplicateActiveRule()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "source", "D411", cancellationToken);
        await EnrollAgentAsync(store, "target-one", "D422", cancellationToken);
        var applier = new RecordingPolicyApplier { ConnectAddsDuplicate = true };
        var service = new LinkService(store, applier, new ControlEventBroker());

        var link = await service.CreateAsync(CreateRequest("target-one"), "operator", cancellationToken);

        Assert.Equal("Failed", link.ActualState);
        Assert.Contains("exactly 1", link.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MarkerIsRetainedWhenPostMutationFactCannotBeVerified()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        var rule = new LinkRule("source", "missing", "tcp", 22);
        var applier = new RecordingPolicyApplier
        {
            DisconnectLeavesRules = true,
            ReconciliationRequest = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
        };
        applier.InjectRule(rule);
        var links = new LinkService(store, applier, new ControlEventBroker());
        var background = CreateBackgroundService(
            links,
            applier,
            new TestTimeProvider(DateTimeOffset.Parse("2026-08-03T12:00:00Z")));

        var result = await background.RunOnceAsync(cancellationToken);

        Assert.NotNull(result);
        Assert.Equal((1, 0, 1), (result.Examined, result.Converged, result.Failed));
        Assert.Equal(0, applier.CompleteReconciliationCalls);
        Assert.NotNull(applier.ReconciliationRequest);
    }

    [Fact]
    public async Task MixedPassClassifiesEveryExaminedPolicyExactlyOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "source", "C011", cancellationToken);
        var applier = new RecordingPolicyApplier();
        var service = new LinkService(store, applier, new ControlEventBroker());
        var links = new List<LinkPolicy>();
        for (var index = 1; index <= 6; index++)
        {
            var target = $"mixed-{index}";
            await EnrollAgentAsync(store, target, $"C0{index}2", cancellationToken);
            links.Add(await service.CreateAsync(CreateRequest(target), "operator", cancellationToken));
        }
        applier.EraseRules();
        applier.InjectRule(ToRule(links[0]));
        applier.InjectRule(ToRule(links[1]));
        applier.FailedMutationRules.Add(ToRule(links[2]));
        applier.FailedMutationRules.Add(ToRule(links[3]));
        applier.DeferredConnectRules.Add(ToRule(links[4]));
        applier.DeferredConnectRules.Add(ToRule(links[5]));

        var result = await service.ReconcileAllAsync(cancellationToken);

        Assert.Equal(6, result.Examined);
        Assert.Equal((2, 2, 2), (result.Converged, result.Failed, result.Deferred));
        Assert.Equal(result.Examined, result.Converged + result.Failed + result.Deferred);
        Assert.Equal(2, result.FailedPolicyIds.Count);
        Assert.Equal(2, result.DeferredPolicyIds.Count);
        Assert.Empty(result.FailedPolicyIds.Intersect(result.DeferredPolicyIds));
    }

    [Fact]
    public async Task DeferredPoliciesConsumeMarkerAndDoNotCreatePromptHotLoop()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "source", "D011", cancellationToken);
        await EnrollAgentAsync(store, "reserved", "D022", cancellationToken);
        var applier = new RecordingPolicyApplier
        {
            ReconciliationRequest = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
        };
        var links = new LinkService(store, applier, new ControlEventBroker());
        var policy = await links.CreateAsync(CreateRequest("reserved"), "operator", cancellationToken);
        applier.EraseRules();
        applier.DeferredConnectRules.Add(ToRule(policy));
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-08-04T12:00:00Z"));
        var logger = new TestLogger<LinkReconciliationBackgroundService>();
        var background = CreateBackgroundService(links, applier, time, logger);

        for (var index = 0; index < 10; index++)
        {
            var result = await background.RunOnceAsync(cancellationToken);
            Assert.NotNull(result);
            Assert.Equal((1, 0, 0, 1),
                (result.Examined, result.Converged, result.Failed, result.Deferred));
            Assert.Equal([policy.Id], result.DeferredPolicyIds);
            if (index == 0)
            {
                Assert.Null(applier.ReconciliationRequest);
                Assert.Equal(1, applier.CompleteReconciliationCalls);
                Assert.Null(await background.RunOnceAsync(cancellationToken));
            }
            time.Advance(TimeSpan.FromSeconds(30));
        }

        Assert.DoesNotContain(logger.Warnings,
            warning => warning.Contains("prompt exhausted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DuplicateManagedRulesCollapseForActiveAndAreRemovedForDisabled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "source", "D111", cancellationToken);
        await EnrollAgentAsync(store, "target-one", "D222", cancellationToken);
        var applier = new RecordingPolicyApplier();
        var service = new LinkService(store, applier, new ControlEventBroker());
        var link = await service.CreateAsync(CreateRequest("target-one"), "operator", cancellationToken);
        applier.InjectRule(new LinkRule("source", "target-one", "tcp", 22));

        await service.ReconcileAllAsync(cancellationToken);
        Assert.Equal(1, applier.RuleCount(link));

        await service.DisableAsync(
            link.Id, new LinkPolicyDisableRequest(Guid.NewGuid().ToString()), "operator", cancellationToken);
        applier.InjectRule(new LinkRule("source", "target-one", "tcp", 22), 2);
        await service.ReconcileAllAsync(cancellationToken);
        Assert.Equal(0, applier.RuleCount(link));
    }

    [Fact]
    public async Task ForeignFirewallRulesAreOutsideManagedListingAndRemainUntouched()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        var applier = new RecordingPolicyApplier { ForeignRuleCount = 1 };
        var service = new LinkService(store, applier, new ControlEventBroker());

        var result = await service.ReconcileAllAsync(cancellationToken);

        Assert.Equal(0, result.Examined);
        Assert.Equal(1, applier.ForeignRuleCount);
        Assert.Equal(0, applier.DisconnectCalls);
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
        await service.DisableAsync(
            active.Id,
            new LinkPolicyDisableRequest(Guid.NewGuid().ToString()),
            "operator",
            cancellationToken);
        var disconnectsBefore = applier.DisconnectCalls;
        applier.InjectRule(new LinkRule("source", "target-one", "tcp", 22));

        await service.ReconcileAllAsync(cancellationToken);

        Assert.Equal(disconnectsBefore + 1, applier.DisconnectCalls);
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
    public async Task FirewallUnavailableDuringFinalBatchListMarksWholePassAndPublishesOneSharedEvent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "source", "F111", cancellationToken);
        await EnrollAgentAsync(store, "target-one", "F222", cancellationToken);
        await EnrollAgentAsync(store, "target-two", "F333", cancellationToken);
        var broker = new ControlEventBroker();
        using var subscription = broker.Subscribe();
        var applier = new RecordingPolicyApplier();
        var service = new LinkService(store, applier, broker);
        var first = await service.CreateAsync(CreateRequest("target-one"), "operator", cancellationToken);
        var second = await service.CreateAsync(CreateRequest("target-two"), "operator", cancellationToken);
        applier.EraseRules();
        applier.UnavailableOnNextPostMutationList = true;
        while (subscription.Reader.TryRead(out _)) { }

        var result = await service.ReconcileAllAsync(cancellationToken);

        Assert.True(result.FirewallUnavailable);
        Assert.Equal(1, applier.MutationCalls(first));
        Assert.Equal(1, applier.MutationCalls(second));
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
        Assert.Single(eventTypes, eventType => eventType == LinkService.FirewallUnavailableCode);
        Assert.Equal(2, eventTypes.Count(eventType => eventType == "link.reconciling"));
        Assert.DoesNotContain("link.reapplied", eventTypes);
        Assert.DoesNotContain("link.orphan-removed", eventTypes);
    }

    [Fact]
    public async Task FactualOrphanAdoptedAsActiveUnderNodeLocksIsNotRemoved()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "source", "F444", cancellationToken);
        await EnrollAgentAsync(store, "target-one", "F555", cancellationToken);
        var applier = new RecordingPolicyApplier();
        var rule = new LinkRule("source", "target-one", "tcp", 22);
        applier.InjectRule(rule);
        var service = new LinkService(store, applier, new ControlEventBroker());
        var nodeLease = await service.AcquireNodeLocksAsync(
            [rule.SourceNodeId, rule.TargetNodeId], cancellationToken);
        var pass = service.ReconcileAllAsync(cancellationToken);
        await applier.ListObserved.Task.WaitAsync(cancellationToken);
        var adopted = (await store.CreateLinkMutationAsync(
            CreateRequest("target-one"), "operator", cancellationToken)).Link;
        nodeLease.Dispose();

        var result = await pass;

        Assert.Equal(0, result.Failed);
        Assert.Equal("Active", (await store.GetLinkAsync(adopted.Id, cancellationToken))!.ActualState);
        Assert.Equal(0, applier.DisconnectCalls);
        Assert.Equal(1, applier.RuleCount(adopted));
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
    public async Task FailingMarkerPassIsPromptedThreeTimesThenUsesRegularThrottle()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "source", "A477", cancellationToken);
        await EnrollAgentAsync(store, "target-one", "B588", cancellationToken);
        var applier = new RecordingPolicyApplier
        {
            ReconciliationRequest = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            FailMutations = true
        };
        var links = new LinkService(store, applier, new ControlEventBroker());
        var link = await links.CreateAsync(CreateRequest("target-one"), "operator", cancellationToken);
        applier.EraseRules();
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-08-03T12:00:00Z"));
        var logger = new TestLogger<LinkReconciliationBackgroundService>();
        var background = CreateBackgroundService(links, applier, time, logger);

        Assert.NotNull(await background.RunOnceAsync(cancellationToken));
        Assert.NotNull(await background.RunOnceAsync(cancellationToken));
        Assert.NotNull(await background.RunOnceAsync(cancellationToken));
        Assert.Null(await background.RunOnceAsync(cancellationToken));
        Assert.NotNull(applier.ReconciliationRequest);
        var warning = Assert.Single(logger.Warnings, message => message.Contains("prompt exhausted", StringComparison.Ordinal));
        Assert.Contains(link.Id, warning, StringComparison.Ordinal);

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.NotNull(await background.RunOnceAsync(cancellationToken));
        Assert.Single(logger.Warnings, message => message.Contains("prompt exhausted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MarkerCompletionFailuresAreAlsoThrottledAndMarkerIsRetained()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        var applier = new RecordingPolicyApplier
        {
            ReconciliationRequest = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            FailCompletion = true
        };
        var links = new LinkService(store, applier, new ControlEventBroker());
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-08-03T12:00:00Z"));
        var background = CreateBackgroundService(links, applier, time);

        Assert.NotNull(await background.RunOnceAsync(cancellationToken));
        Assert.NotNull(await background.RunOnceAsync(cancellationToken));
        Assert.NotNull(await background.RunOnceAsync(cancellationToken));
        Assert.Null(await background.RunOnceAsync(cancellationToken));
        Assert.NotNull(applier.ReconciliationRequest);
        Assert.Equal(3, applier.CompleteReconciliationCalls);
    }

    [Fact]
    public async Task GenericMarkerPassFailuresArePromptedThreeTimesThenUseRegularThrottle()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        var applier = new RecordingPolicyApplier
        {
            ReconciliationRequest = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            FailList = true
        };
        var links = new LinkService(store, applier, new ControlEventBroker());
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-08-03T12:00:00Z"));
        var background = CreateBackgroundService(links, applier, time);

        await Assert.ThrowsAsync<InvalidOperationException>(() => background.RunOnceAsync(cancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => background.RunOnceAsync(cancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => background.RunOnceAsync(cancellationToken));
        Assert.Null(await background.RunOnceAsync(cancellationToken));
        Assert.NotNull(applier.ReconciliationRequest);
        Assert.Equal(3, applier.ListCalls);

        time.Advance(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<InvalidOperationException>(() => background.RunOnceAsync(cancellationToken));
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
        TimeProvider timeProvider,
        ILogger<LinkReconciliationBackgroundService>? logger = null)
        => new(
            links,
            applier,
            Options.Create(new ControlOptions { LinkReconciliationSeconds = 30 }),
            timeProvider,
            logger ?? NullLogger<LinkReconciliationBackgroundService>.Instance);

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
        private readonly List<LinkRule> _rules = [];
        private readonly Dictionary<LinkRule, int> _mutationCalls = [];
        public TaskCompletionSource ListObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PausedListObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePausedList { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int ListCalls { get; private set; }
        public int? PauseOnListCall { get; set; }
        public int ConnectCalls { get; private set; }
        public int DisconnectCalls { get; private set; }
        public int StatusCalls { get; private set; }
        public int PrivilegedCalls => ListCalls + ConnectCalls + DisconnectCalls + StatusCalls;
        public bool FirewallUnavailable { get; set; }
        public bool FailMutations { get; set; }
        public bool FailList { get; set; }
        public bool UnavailableOnNextMutation { get; set; }
        public bool UnavailableOnNextPostMutationList { get; set; }
        public bool DisconnectLeavesRules { get; set; }
        public bool ConnectAddsDuplicate { get; set; }
        public bool ThrowNodeNotActivatedOnStatus { get; set; }
        public HashSet<LinkRule> FailedMutationRules { get; } = [];
        public HashSet<LinkRule> DeferredConnectRules { get; } = [];
        public string? ReconciliationRequest { get; set; }
        public string? ReplacementRequestOnNextProbe { get; set; }
        public int ReconciliationStatusCalls { get; private set; }
        public int CompleteReconciliationCalls { get; private set; }
        public bool FailCompletion { get; set; }
        public int ForeignRuleCount { get; set; }
        public List<string> CompletedGenerations { get; } = [];

        public void EraseRules()
        {
            _rules.Clear();
            _mutationCalls.Clear();
        }
        public void InjectRule(LinkRule rule, int count = 1)
        {
            for (var index = 0; index < count; index++)
            {
                _rules.Add(rule);
            }
        }
        public int RuleCount(LinkPolicy link) => _rules.Count(rule => rule == ToRule(link));
        public int MutationCalls(LinkPolicy link)
            => _mutationCalls.GetValueOrDefault(ToRule(link));
        public bool IsConnected(LinkPolicy link) => RuleCount(link) > 0;

        public async Task<IReadOnlyList<LinkRule>> ListRulesAsync(CancellationToken cancellationToken)
        {
            ListCalls++;
            ListObserved.TrySetResult();
            ReplaceRequestIfNeeded();
            if (UnavailableOnNextPostMutationList && _mutationCalls.Count > 0)
            {
                UnavailableOnNextPostMutationList = false;
                throw new MeshFirewallUnavailableException(LinkService.FirewallUnavailableCode);
            }
            if (FailList)
            {
                throw new InvalidOperationException("simulated link-list failure");
            }
            if (FirewallUnavailable)
            {
                throw new MeshFirewallUnavailableException(LinkService.FirewallUnavailableCode);
            }
            var result = _rules.ToArray();
            if (ListCalls == PauseOnListCall)
            {
                PausedListObserved.TrySetResult();
                await ReleasePausedList.Task.WaitAsync(cancellationToken);
            }
            return result;
        }

        public Task ApplyConnectAsync(LinkPolicy link, CancellationToken cancellationToken)
        {
            ConnectCalls++;
            var rule = ToRule(link);
            RecordMutation(rule);
            ThrowIfMutationUnavailable();
            if (DeferredConnectRules.Contains(rule))
            {
                throw new MeshNodeNotActivatedException(LinkService.NodeNotActivatedCode);
            }
            if (FailMutations || FailedMutationRules.Contains(rule))
            {
                throw new InvalidOperationException("simulated policy failure");
            }
            _rules.Add(rule);
            if (ConnectAddsDuplicate)
            {
                _rules.Add(ToRule(link));
            }
            return Task.CompletedTask;
        }

        public Task ApplyDisconnectAsync(LinkPolicy link, CancellationToken cancellationToken)
            => ApplyDisconnectAsync(ToRule(link), cancellationToken);

        public Task ApplyDisconnectAsync(LinkRule rule, CancellationToken cancellationToken)
        {
            DisconnectCalls++;
            RecordMutation(rule);
            ThrowIfMutationUnavailable();
            if (FailMutations)
            {
                throw new InvalidOperationException("simulated policy failure");
            }
            if (!DisconnectLeavesRules)
            {
                _rules.RemoveAll(candidate => candidate == rule);
            }
            return Task.CompletedTask;
        }

        public Task<bool> IsConnectedAsync(LinkPolicy link, CancellationToken cancellationToken)
        {
            StatusCalls++;
            ReplaceRequestIfNeeded();
            if (ThrowNodeNotActivatedOnStatus)
            {
                throw new MeshNodeNotActivatedException(LinkService.NodeNotActivatedCode);
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
            if (FailCompletion)
            {
                throw new InvalidOperationException("simulated marker completion failure");
            }
            CompletedGenerations.Add(generation);
            if (ReconciliationRequest == generation)
            {
                ReconciliationRequest = null;
            }
            return Task.CompletedTask;
        }

        private void ReplaceRequestIfNeeded()
        {
            if (ReplacementRequestOnNextProbe is null)
            {
                return;
            }
            ReconciliationRequest = ReplacementRequestOnNextProbe;
            ReplacementRequestOnNextProbe = null;
        }

        private void RecordMutation(LinkRule rule)
            => _mutationCalls[rule] = _mutationCalls.GetValueOrDefault(rule) + 1;

        private void ThrowIfMutationUnavailable()
        {
            if (!UnavailableOnNextMutation)
            {
                return;
            }
            UnavailableOnNextMutation = false;
            throw new MeshFirewallUnavailableException(LinkService.FirewallUnavailableCode);
        }

        private static LinkRule ToRule(LinkPolicy link)
            => new(link.SourceNodeId, link.TargetNodeId, link.Protocol, link.Port);
    }

    private static LinkRule ToRule(LinkPolicy link)
        => new(link.SourceNodeId, link.TargetNodeId, link.Protocol, link.Port);

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
