using System.Collections.Concurrent;
using System.Text.Json;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Control;

public sealed class LinkService(
    ControlStore store,
    ILinkPolicyApplier applier,
    ControlEventBroker events)
{
    public const string FirewallUnavailableCode = "mesh.firewall-unavailable";
    public const string FirewallAvailableCode = "mesh.firewall-available";
    public const string NodeNotActivatedCode = "mesh.node-not-activated";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _reconciliationLocks = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _nodeLocks = new();

    public async Task<LinkPolicy> CreateAsync(
        LinkPolicyCreateRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        using var nodeLease = await AcquireNodeLocksAsync(
            [request.SourceNodeId, request.TargetNodeId], cancellationToken);
        var mutation = await store.CreateLinkMutationAsync(request, actor, cancellationToken);
        var link = mutation.Link;
        if (mutation.IsReplay && link.ActualState != "Connecting")
        {
            return link;
        }
        var gate = await AcquireLinkGateAsync(link.Id, cancellationToken);
        try
        {
            var current = await store.GetLinkAsync(link.Id, cancellationToken)
                ?? throw new InvalidOperationException("The persisted Link disappeared.");
            if (current.DesiredState != "Active" || !await store.IsEffectiveLinkAsync(current, cancellationToken))
            {
                return current;
            }
            if (!mutation.IsReplay)
            {
                Publish("link.connecting", current);
            }
            return await ConvergeAsync(current, expectedConnected: true, actor, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<LinkPolicy?> DisableAsync(
        string id,
        LinkPolicyDisableRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        var gate = await AcquireLinkGateAsync(id, cancellationToken);
        try
        {
            return await DisableCoreAsync(id, request, actor, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<LinkPolicy?> DisableCoreAsync(
        string id,
        LinkPolicyDisableRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        var mutation = await store.BeginDisableLinkMutationAsync(id, request, actor, cancellationToken);
        if (mutation is null)
        {
            return null;
        }
        var link = mutation.Link;
        if (mutation.IsReplay && link.ActualState == "Disabled")
        {
            return link;
        }
        if (!mutation.IsReplay)
        {
            Publish("link.disconnecting", link);
        }
        return await ConvergeAsync(link, expectedConnected: false, actor, cancellationToken);
    }

    internal async Task<LinkPolicy> ConvergeDisabledAsync(
        LinkPolicy link,
        string actor,
        CancellationToken cancellationToken)
    {
        var gate = await AcquireLinkGateAsync(link.Id, cancellationToken);
        try
        {
            return await ConvergeAsync(link, expectedConnected: false, actor, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<LinkReconciliationResult> ReconcileLinksForNodeAsync(
        string nodeId,
        CancellationToken cancellationToken)
    {
        var examined = 0;
        var converged = 0;
        var deferred = 0;
        var failed = 0;
        var failedPolicyIds = new List<string>();
        var deferredPolicyIds = new List<string>();
        var links = await store.ListEffectiveLinksForNodeAsync(nodeId, cancellationToken);
        foreach (var candidate in links)
        {
            using var nodeLease = await AcquireNodeLocksAsync(
                [candidate.SourceNodeId, candidate.TargetNodeId], cancellationToken);
            var gate = await AcquireLinkGateAsync(candidate.Id, cancellationToken);
            try
            {
                var current = await GetCurrentEffectiveAsync(candidate.Id, cancellationToken);
                if (current is null || (current.SourceNodeId != nodeId && current.TargetNodeId != nodeId))
                {
                    continue;
                }
                var result = await ConvergeAsync(
                    current,
                    current.DesiredState == "Active",
                    $"system:reconnect:{nodeId}",
                    cancellationToken);
                examined++;
                if (result.ActualState is "Failed" or "Partial")
                {
                    failed++;
                    failedPolicyIds.Add(result.Id);
                }
                else if (result.ActualState == "PendingActivation")
                {
                    deferred++;
                    deferredPolicyIds.Add(result.Id);
                }
                else if (result.ActualState == (current.DesiredState == "Active" ? "Active" : "Disabled"))
                {
                    converged++;
                }
            }
            finally
            {
                gate.Release();
            }
        }
        return new LinkReconciliationResult(
            examined, converged, failed, deferred, failedPolicyIds, deferredPolicyIds);
    }

    public async Task<LinkFullReconciliationResult> ReconcileAllAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LinkRule> factualRules;
        try
        {
            factualRules = await applier.ListRulesAsync(cancellationToken);
        }
        catch (MeshFirewallUnavailableException)
        {
            var unavailableCandidates = await store.ListEffectiveLinksAsync(cancellationToken);
            return await CompleteFirewallUnavailablePassAsync(
                unavailableCandidates, 0, 0, cancellationToken);
        }

        var candidates = await store.ListEffectiveLinksAsync(cancellationToken);
        var recoveringFirewall = candidates.Any(candidate =>
            string.Equals(candidate.LastError, FirewallUnavailableCode, StringComparison.Ordinal));
        var factualCounts = factualRules
            .GroupBy(static rule => rule)
            .ToDictionary(static group => group.Key, static group => group.Count());
        var processedKeys = new HashSet<LinkRule>();
        var batch = new FullReconciliationBatch();
        var pendingClassifications = new List<(string Id, string ExpectedState)>();
        var examined = 0;
        var converged = 0;
        var deferred = 0;
        var failed = 0;
        var failedPolicyIds = new List<string>();
        var deferredPolicyIds = new List<string>();

        foreach (var candidate in candidates)
        {
            var firewallUnavailable = false;
            using (await AcquireNodeLocksAsync(
                [candidate.SourceNodeId, candidate.TargetNodeId], cancellationToken))
            {
                var candidateRule = ToRule(candidate);
                var selected = await store.GetEffectiveLinkAsync(candidateRule, cancellationToken);
                if (selected is null)
                {
                    continue;
                }
                var gate = await AcquireLinkGateAsync(selected.Id, cancellationToken);
                try
                {
                    var current = await store.GetEffectiveLinkAsync(candidateRule, cancellationToken);
                    if (current is null)
                    {
                        continue;
                    }
                    var rule = ToRule(current);
                    if (!processedKeys.Add(rule))
                    {
                        continue;
                    }
                    factualCounts.TryGetValue(rule, out var factualCount);
                    var result = await ConvergeAsync(
                        current,
                        current.DesiredState == "Active",
                        "system:reconcile",
                        cancellationToken,
                        factualCount,
                        batch: batch);
                    examined++;
                    if (result.LastError == FirewallUnavailableCode)
                    {
                        firewallUnavailable = true;
                    }
                    else if (result.ActualState is "Failed" or "Partial")
                    {
                        failed++;
                        failedPolicyIds.Add(result.Id);
                    }
                    else if (result.ActualState == "PendingActivation")
                    {
                        deferred++;
                        deferredPolicyIds.Add(result.Id);
                    }
                    else if (result.ActualState == (current.DesiredState == "Active" ? "Active" : "Disabled"))
                    {
                        converged++;
                    }
                    else if (batch.Contains(result.Id))
                    {
                        pendingClassifications.Add((
                            result.Id,
                            current.DesiredState == "Active" ? "Active" : "Disabled"));
                    }
                    else
                    {
                        failed++;
                        failedPolicyIds.Add(result.Id);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }
            if (firewallUnavailable)
            {
                return await CompleteFirewallUnavailablePassAsync(
                    candidates, examined, converged, cancellationToken);
            }
        }

        foreach (var orphan in factualCounts.Keys.Where(rule => !processedKeys.Contains(rule)))
        {
            var firewallUnavailable = false;
            using (await AcquireNodeLocksAsync(
                [orphan.SourceNodeId, orphan.TargetNodeId], cancellationToken))
            {
                var selected = await store.GetEffectiveLinkAsync(orphan, cancellationToken);
                var gate = await AcquireLinkGateAsync(
                    selected?.Id
                        ?? $"orphan:{orphan.SourceNodeId}:{orphan.TargetNodeId}:{orphan.Protocol}:{orphan.Port}",
                    cancellationToken);
                try
                {
                    var persisted = await store.GetEffectiveLinkAsync(orphan, cancellationToken);
                    var target = persisted ?? new LinkPolicy(
                        $"orphan:{orphan.SourceNodeId}:{orphan.TargetNodeId}:{orphan.Protocol}:{orphan.Port}",
                        orphan.SourceNodeId,
                        orphan.TargetNodeId,
                        orphan.Protocol,
                        orphan.Port,
                        0,
                        "factual orphan",
                        "Disabled",
                        "Active",
                        0,
                        DateTimeOffset.UtcNow,
                        null,
                        DateTimeOffset.UtcNow,
                        null);
                    var result = await ConvergeAsync(
                        target,
                        expectedConnected: persisted?.DesiredState == "Active",
                        "system:reconcile",
                        cancellationToken,
                        factualCounts[orphan],
                        persisted: persisted is not null,
                        batch: batch);
                    examined++;
                    var expectedState = persisted?.DesiredState == "Active" ? "Active" : "Disabled";
                    if (result.LastError == FirewallUnavailableCode)
                    {
                        firewallUnavailable = true;
                    }
                    else if (result.ActualState == expectedState)
                    {
                        converged++;
                    }
                    else if (result.ActualState == "PendingActivation")
                    {
                        deferred++;
                        deferredPolicyIds.Add(result.Id);
                    }
                    else if (batch.Contains(result.Id))
                    {
                        pendingClassifications.Add((result.Id, expectedState));
                    }
                    else
                    {
                        failed++;
                        failedPolicyIds.Add(result.Id);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }
            if (firewallUnavailable)
            {
                return await CompleteFirewallUnavailablePassAsync(
                    candidates, examined, converged, cancellationToken);
            }
        }

        if (batch.MutationAttempted)
        {
            IReadOnlyDictionary<string, LinkPolicy> finalized;
            try
            {
                finalized = await FinalizeBatchAsync(batch, cancellationToken);
            }
            catch (MeshFirewallUnavailableException)
            {
                return await CompleteFirewallUnavailablePassAsync(
                    candidates, examined, converged, cancellationToken);
            }
            foreach (var pendingClassification in pendingClassifications)
            {
                var result = finalized[pendingClassification.Id];
                if (result.ActualState == pendingClassification.ExpectedState)
                {
                    converged++;
                }
                else if (result.ActualState == "PendingActivation")
                {
                    deferred++;
                    deferredPolicyIds.Add(result.Id);
                }
                else
                {
                    failed++;
                    failedPolicyIds.Add(result.Id);
                }
            }
        }

        if (recoveringFirewall)
        {
            events.Publish(
                FirewallAvailableCode,
                "mesh",
                JsonSerializer.Serialize(
                    new ControlError(FirewallAvailableCode), SmmJsonContext.Default.ControlError));
        }
        return new LinkFullReconciliationResult(
            examined,
            converged,
            failed,
            deferred,
            false,
            failedPolicyIds,
            deferredPolicyIds);
    }

    public async Task<LinkExpirationResult> ExpireDueLinksAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var disabled = 0;
        var failed = 0;
        var links = await store.ListExpiredLinksAsync(now, cancellationToken);
        foreach (var candidate in links)
        {
            var gate = await AcquireLinkGateAsync(candidate.Id, cancellationToken);
            try
            {
                var current = await store.GetLinkAsync(candidate.Id, cancellationToken);
                if (current is null || current.ExpiresAt is null || current.ExpiresAt > now
                    || !await store.IsEffectiveLinkAsync(current, cancellationToken))
                {
                    continue;
                }
                LinkPolicy? result;
                if (current.DesiredState == "Active")
                {
                    result = await DisableCoreAsync(
                        current.Id,
                        new LinkPolicyDisableRequest(Guid.NewGuid().ToString()),
                        "system:ttl",
                        cancellationToken);
                }
                else
                {
                    Publish("link.disconnecting", current);
                    result = await ConvergeAsync(
                        current, expectedConnected: false, "system:ttl-retry", cancellationToken);
                }
                if (result?.ActualState == "Disabled")
                {
                    disabled++;
                }
                else
                {
                    failed++;
                }
            }
            finally
            {
                gate.Release();
            }
        }
        return new LinkExpirationResult(disabled, failed);
    }

    private async Task<LinkPolicy> ConvergeAsync(
        LinkPolicy link,
        bool expectedConnected,
        string actor,
        CancellationToken cancellationToken,
        int? knownFactualCount = null,
        bool persisted = true,
        FullReconciliationBatch? batch = null)
    {
        var current = persisted
            ? await store.GetLinkAsync(link.Id, cancellationToken) ?? link
            : link;
        if (persisted
            && ((current.DesiredState == "Active") != expectedConnected
                || !await store.IsEffectiveLinkAsync(current, cancellationToken)))
        {
            return current;
        }

        var completedState = expectedConnected ? "Active" : "Disabled";
        var failureState = expectedConnected ? "Failed" : "Partial";
        var completedEvent = expectedConnected ? "link.active" : "link.disabled";
        var failureEvent = expectedConnected ? "link.failed" : "link.partial";
        try
        {
            var factualCount = knownFactualCount
                ?? await CountExactRulesAsync(current, cancellationToken);
            var isConnected = factualCount > 0;
            var duplicateActiveRule = expectedConnected && factualCount > 1;
            var changedFact = isConnected != expectedConnected || duplicateActiveRule;
            if (changedFact)
            {
                var pendingState = expectedConnected ? "Connecting" : "Disconnecting";
                if (persisted && (current.ActualState != pendingState || current.LastError is not null))
                {
                    current = await store.SetLinkActualStateAsync(
                        current.Id, pendingState, null, actor, cancellationToken) ?? current;
                }
                if (duplicateActiveRule)
                {
                    batch?.MarkMutationAttempted();
                    await applier.ApplyDisconnectAsync(current, cancellationToken);
                    if (batch is null)
                    {
                        await VerifyExactFactualCountAsync(current, 0, cancellationToken);
                    }
                }
                if (expectedConnected)
                {
                    batch?.MarkMutationAttempted();
                    await applier.ApplyConnectAsync(current, cancellationToken);
                    if (batch is null)
                    {
                        await VerifyExactFactualCountAsync(current, 1, cancellationToken);
                    }
                }
                else if (persisted)
                {
                    batch?.MarkMutationAttempted();
                    await applier.ApplyDisconnectAsync(current, cancellationToken);
                    if (batch is null)
                    {
                        await VerifyExactFactualCountAsync(current, 0, cancellationToken);
                    }
                }
                else
                {
                    batch?.MarkMutationAttempted();
                    await applier.ApplyDisconnectAsync(ToRule(current), cancellationToken);
                    if (batch is null)
                    {
                        await VerifyExactFactualCountAsync(current, 0, cancellationToken);
                    }
                }
                if (actor.StartsWith("system:", StringComparison.Ordinal))
                {
                    Publish("link.reconciling", current);
                }
                if (batch is not null)
                {
                    batch.Stage(current, expectedConnected, actor, persisted);
                    return current;
                }
            }
            current = await FinalizeConvergenceAsync(
                current, expectedConnected, actor, persisted, changedFact, cancellationToken);
        }
        catch (MeshNodeNotActivatedException) when (expectedConnected)
        {
            if (persisted)
            {
                current = await store.SetLinkActualStateAsync(
                    current.Id, "PendingActivation", NodeNotActivatedCode, actor, cancellationToken) ?? current;
                Publish("link.pending-node-activation", current);
            }
            else
            {
                current = current with { ActualState = "PendingActivation", LastError = NodeNotActivatedCode };
            }
        }
        catch (MeshFirewallUnavailableException)
        {
            current = persisted
                ? await store.SetLinkActualStateAsync(
                    current.Id, failureState, FirewallUnavailableCode, actor, cancellationToken) ?? current
                : current with { ActualState = failureState, LastError = FirewallUnavailableCode };
            if (actor != "system:reconcile")
            {
                Publish(failureEvent, current);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var error = CompactError(exception);
            current = persisted
                ? await store.SetLinkActualStateAsync(
                    current.Id, failureState, error, actor, cancellationToken) ?? current
                : current with { ActualState = failureState, LastError = error };
            Publish(failureEvent, current);
        }
        return current;
    }

    private async Task<IReadOnlyDictionary<string, LinkPolicy>> FinalizeBatchAsync(
        FullReconciliationBatch batch,
        CancellationToken cancellationToken)
    {
        var pendingItems = batch.Pending.ToArray();
        using var nodeLease = await AcquireNodeLocksAsync(
            pendingItems.SelectMany(static pending =>
                new[] { pending.Link.SourceNodeId, pending.Link.TargetNodeId }),
            cancellationToken);
        var selectedByPendingId = new Dictionary<string, LinkPolicy?>(StringComparer.Ordinal);
        foreach (var pending in pendingItems)
        {
            selectedByPendingId[pending.Link.Id] = await store.GetEffectiveLinkAsync(
                ToRule(pending.Link), cancellationToken);
        }
        using var linkLease = await AcquireLinkGatesAsync(
            pendingItems.Select(pending =>
                selectedByPendingId[pending.Link.Id]?.Id ?? pending.Link.Id),
            cancellationToken);
        var finalRules = await applier.ListRulesAsync(cancellationToken);
        var currentByPendingId = new Dictionary<string, LinkPolicy?>(StringComparer.Ordinal);
        foreach (var pending in pendingItems)
        {
            currentByPendingId[pending.Link.Id] = await store.GetEffectiveLinkAsync(
                ToRule(pending.Link), cancellationToken);
        }
        var finalCounts = finalRules
            .GroupBy(static rule => rule)
            .ToDictionary(static group => group.Key, static group => group.Count());
        var finalized = new Dictionary<string, LinkPolicy>(StringComparer.Ordinal);
        foreach (var pending in pendingItems)
        {
            var current = currentByPendingId[pending.Link.Id];
            if (IsStale(pending, current))
            {
                finalized.Add(pending.Link.Id, CreateStaleBatchResult(pending, current));
                continue;
            }
            finalCounts.TryGetValue(ToRule(pending.Link), out var actualCount);
            var expectedCount = pending.ExpectedConnected ? 1 : 0;
            LinkPolicy result;
            if (actualCount == expectedCount)
            {
                result = await FinalizeConvergenceAsync(
                    pending.Link,
                    pending.ExpectedConnected,
                    pending.Actor,
                    pending.Persisted,
                    changedFact: true,
                    cancellationToken);
            }
            else
            {
                result = await FailConvergenceAsync(
                    pending.Link,
                    pending.ExpectedConnected,
                    pending.Actor,
                    pending.Persisted,
                    $"Factual Link policy count must be exactly {expectedCount} after application, but was {actualCount}.",
                    cancellationToken);
            }
            finalized.Add(pending.Link.Id, result);
        }
        return finalized;
    }

    private static bool IsStale(PendingConvergence pending, LinkPolicy? current)
        => pending.Persisted
            ? current is null
                || current.Id != pending.Link.Id
                || current.Version != pending.Link.Version
                || (current.DesiredState == "Active") != pending.ExpectedConnected
            : current is not null;

    private static LinkPolicy CreateStaleBatchResult(
        PendingConvergence pending,
        LinkPolicy? current)
        => (current ?? pending.Link) with
        {
            ActualState = pending.ExpectedConnected ? "Failed" : "Partial",
            LastError = "Link policy changed before batch finalization."
        };

    private async Task<LinkPolicy> FinalizeConvergenceAsync(
        LinkPolicy current,
        bool expectedConnected,
        string actor,
        bool persisted,
        bool changedFact,
        CancellationToken cancellationToken)
    {
        var completedState = expectedConnected ? "Active" : "Disabled";
        var completedEvent = expectedConnected ? "link.active" : "link.disabled";
        if (persisted && (current.ActualState != completedState || current.LastError is not null))
        {
            current = await store.SetLinkActualStateAsync(
                current.Id, completedState, null, actor, cancellationToken) ?? current;
            Publish(completedEvent, current);
        }
        else if (!persisted)
        {
            current = current with { ActualState = completedState, LastError = null };
        }
        if (changedFact && actor == "system:reconcile")
        {
            if (!expectedConnected)
            {
                await store.RecordLinkOrphanRemovedAsync(ToRule(current), actor, cancellationToken);
            }
            Publish(expectedConnected ? "link.reapplied" : "link.orphan-removed", current);
        }
        return current;
    }

    private async Task<LinkPolicy> FailConvergenceAsync(
        LinkPolicy current,
        bool expectedConnected,
        string actor,
        bool persisted,
        string error,
        CancellationToken cancellationToken)
    {
        var failureState = expectedConnected ? "Failed" : "Partial";
        var failureEvent = expectedConnected ? "link.failed" : "link.partial";
        current = persisted
            ? await store.SetLinkActualStateAsync(
                current.Id, failureState, error, actor, cancellationToken) ?? current
            : current with { ActualState = failureState, LastError = error };
        Publish(failureEvent, current);
        return current;
    }

    private async Task<LinkPolicy?> GetCurrentEffectiveAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var current = await store.GetLinkAsync(id, cancellationToken);
        if (current is null || !await store.IsEffectiveLinkAsync(current, cancellationToken))
        {
            return null;
        }
        return current;
    }

    private async Task MarkFirewallUnavailableAsync(
        IReadOnlyList<LinkPolicy> candidates,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            using var nodeLease = await AcquireNodeLocksAsync(
                [candidate.SourceNodeId, candidate.TargetNodeId], cancellationToken);
            var rule = ToRule(candidate);
            var selected = await store.GetEffectiveLinkAsync(rule, cancellationToken);
            if (selected is null)
            {
                continue;
            }
            var gate = await AcquireLinkGateAsync(selected.Id, cancellationToken);
            try
            {
                var current = await store.GetEffectiveLinkAsync(rule, cancellationToken);
                if (current is not null)
                {
                    await store.SetLinkActualStateAsync(
                        current.Id, "Partial", FirewallUnavailableCode, "system:reconcile", cancellationToken);
                }
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task<LinkFullReconciliationResult> CompleteFirewallUnavailablePassAsync(
        IReadOnlyList<LinkPolicy> candidates,
        int examined,
        int converged,
        CancellationToken cancellationToken)
    {
        await MarkFirewallUnavailableAsync(candidates, cancellationToken);
        events.Publish(
            FirewallUnavailableCode,
            "mesh",
            JsonSerializer.Serialize(
                new ControlError(FirewallUnavailableCode), SmmJsonContext.Default.ControlError));
        var failed = examined - converged;
        var failedPolicyIds = candidates
            .Take(failed)
            .Select(static candidate => candidate.Id)
            .ToArray();
        return new LinkFullReconciliationResult(
            examined, converged, failed, 0, true, failedPolicyIds, []);
    }

    private static LinkRule ToRule(LinkPolicy link)
        => new(link.SourceNodeId, link.TargetNodeId, link.Protocol, link.Port);

    private void Publish(string type, LinkPolicy link)
        => events.Publish(
            type,
            link.Id,
            JsonSerializer.Serialize(link, SmmJsonContext.Default.LinkPolicy));

    internal async Task<IDisposable> AcquireNodeLocksAsync(
        IEnumerable<string> nodeIds,
        CancellationToken cancellationToken)
    {
        var gates = nodeIds
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(nodeId => _nodeLocks.GetOrAdd(nodeId, static _ => new SemaphoreSlim(1, 1)))
            .ToArray();
        var acquired = 0;
        try
        {
            foreach (var gate in gates)
            {
                await gate.WaitAsync(cancellationToken);
                acquired++;
            }
            return new LockLease(gates);
        }
        catch
        {
            for (var index = acquired - 1; index >= 0; index--)
            {
                gates[index].Release();
            }
            throw;
        }
    }

    private async Task<SemaphoreSlim> AcquireLinkGateAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var gate = _reconciliationLocks.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return gate;
    }

    private async Task<IDisposable> AcquireLinkGatesAsync(
        IEnumerable<string> ids,
        CancellationToken cancellationToken)
    {
        var gates = ids
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(id => _reconciliationLocks.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1)))
            .ToArray();
        var acquired = 0;
        try
        {
            foreach (var gate in gates)
            {
                await gate.WaitAsync(cancellationToken);
                acquired++;
            }
            return new LockLease(gates);
        }
        catch
        {
            for (var index = acquired - 1; index >= 0; index--)
            {
                gates[index].Release();
            }
            throw;
        }
    }

    private async Task<int> CountExactRulesAsync(
        LinkPolicy link,
        CancellationToken cancellationToken)
        => (await applier.ListRulesAsync(cancellationToken)).Count(rule => rule == ToRule(link));

    private async Task VerifyExactFactualCountAsync(
        LinkPolicy link,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        var actualCount = await CountExactRulesAsync(link, cancellationToken);
        if (actualCount != expectedCount)
        {
            throw new InvalidOperationException(
                $"Factual Link policy count must be exactly {expectedCount} after application, but was {actualCount}.");
        }
    }

    private static string CompactError(Exception exception)
        => exception.Message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "Policy application failed.";

    private sealed class LockLease(SemaphoreSlim[] gates) : IDisposable
    {
        public void Dispose()
        {
            for (var index = gates.Length - 1; index >= 0; index--)
            {
                gates[index].Release();
            }
        }
    }

    private sealed class FullReconciliationBatch
    {
        private readonly Dictionary<string, PendingConvergence> _pending =
            new(StringComparer.Ordinal);

        public bool MutationAttempted { get; private set; }
        public IReadOnlyCollection<PendingConvergence> Pending => _pending.Values;

        public void MarkMutationAttempted() => MutationAttempted = true;

        public void Stage(LinkPolicy link, bool expectedConnected, string actor, bool persisted)
            => _pending[link.Id] = new PendingConvergence(link, expectedConnected, actor, persisted);

        public bool Contains(string id) => _pending.ContainsKey(id);
    }

    private sealed record PendingConvergence(
        LinkPolicy Link,
        bool ExpectedConnected,
        string Actor,
        bool Persisted);
}

public sealed record LinkExpirationResult(int Disabled, int Failed);
public sealed record LinkFullReconciliationResult
{
    public LinkFullReconciliationResult(
        int examined,
        int converged,
        int failed,
        int deferred,
        bool firewallUnavailable,
        IReadOnlyList<string> failedPolicyIds,
        IReadOnlyList<string> deferredPolicyIds)
    {
        if (converged + failed + deferred != examined)
        {
            throw new InvalidOperationException(
                $"Full Link reconciliation classification invariant failed: examined={examined}, "
                + $"converged={converged}, failed={failed}, deferred={deferred}; "
                + $"failed IDs=[{string.Join(',', failedPolicyIds)}], "
                + $"deferred IDs=[{string.Join(',', deferredPolicyIds)}].");
        }
        Examined = examined;
        Converged = converged;
        Failed = failed;
        Deferred = deferred;
        FirewallUnavailable = firewallUnavailable;
        FailedPolicyIds = failedPolicyIds;
        DeferredPolicyIds = deferredPolicyIds;
    }

    public int Examined { get; }
    public int Converged { get; }
    public int Failed { get; }
    public int Deferred { get; }
    public bool FirewallUnavailable { get; }
    public IReadOnlyList<string> FailedPolicyIds { get; }
    public IReadOnlyList<string> DeferredPolicyIds { get; }
}
