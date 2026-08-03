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
        var reconciled = 0;
        var failed = 0;
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
                reconciled++;
                if (result.ActualState is "Failed" or "Partial")
                {
                    failed++;
                }
            }
            finally
            {
                gate.Release();
            }
        }
        return new LinkReconciliationResult(reconciled, failed);
    }

    public async Task<LinkFullReconciliationResult> ReconcileAllAsync(
        CancellationToken cancellationToken = default)
    {
        var candidates = await store.ListEffectiveLinksAsync(cancellationToken);
        var recoveringFirewall = candidates.Any(candidate =>
            string.Equals(candidate.LastError, FirewallUnavailableCode, StringComparison.Ordinal));
        var examined = 0;
        var failed = 0;
        var firewallUnavailable = false;
        foreach (var candidate in candidates)
        {
            using var nodeLease = await AcquireNodeLocksAsync(
                [candidate.SourceNodeId, candidate.TargetNodeId], cancellationToken);
            var gate = await AcquireLinkGateAsync(candidate.Id, cancellationToken);
            try
            {
                var current = await GetCurrentEffectiveAsync(candidate.Id, cancellationToken);
                if (current is null || !IsEligibleForFullReconciliation(current))
                {
                    continue;
                }
                var result = await ConvergeAsync(
                    current, current.DesiredState == "Active", "system:reconcile", cancellationToken);
                examined++;
                if (result.LastError == FirewallUnavailableCode)
                {
                    firewallUnavailable = true;
                }
                else if (result.ActualState is "Failed" or "Partial")
                {
                    failed++;
                }
            }
            finally
            {
                gate.Release();
            }
            if (firewallUnavailable)
            {
                break;
            }
        }
        if (firewallUnavailable)
        {
            await MarkFirewallUnavailableAsync(candidates, cancellationToken);
            events.Publish(
                FirewallUnavailableCode,
                "mesh",
                JsonSerializer.Serialize(
                    new ControlError(FirewallUnavailableCode), SmmJsonContext.Default.ControlError));
            return new LinkFullReconciliationResult(examined, candidates.Count, true);
        }
        if (recoveringFirewall)
        {
            events.Publish(
                FirewallAvailableCode,
                "mesh",
                JsonSerializer.Serialize(
                    new ControlError(FirewallAvailableCode), SmmJsonContext.Default.ControlError));
        }
        return new LinkFullReconciliationResult(examined, failed, false);
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
        CancellationToken cancellationToken)
    {
        var current = await store.GetLinkAsync(link.Id, cancellationToken) ?? link;
        if ((current.DesiredState == "Active") != expectedConnected
            || !await store.IsEffectiveLinkAsync(current, cancellationToken))
        {
            return current;
        }

        var completedState = expectedConnected ? "Active" : "Disabled";
        var failureState = expectedConnected ? "Failed" : "Partial";
        var completedEvent = expectedConnected ? "link.active" : "link.disabled";
        var failureEvent = expectedConnected ? "link.failed" : "link.partial";
        try
        {
            var isConnected = await applier.IsConnectedAsync(current, cancellationToken);
            var changedFact = isConnected != expectedConnected;
            if (changedFact)
            {
                var pendingState = expectedConnected ? "Connecting" : "Disconnecting";
                if (current.ActualState != pendingState || current.LastError is not null)
                {
                    current = await store.SetLinkActualStateAsync(
                        current.Id, pendingState, null, actor, cancellationToken) ?? current;
                }
                if (actor.StartsWith("system:", StringComparison.Ordinal))
                {
                    Publish("link.reconciling", current);
                }
                if (expectedConnected)
                {
                    await applier.ApplyConnectAsync(current, cancellationToken);
                }
                else
                {
                    await applier.ApplyDisconnectAsync(current, cancellationToken);
                }
                await VerifyFactualStateAsync(current, expectedConnected, cancellationToken);
            }
            if (current.ActualState != completedState || current.LastError is not null)
            {
                current = await store.SetLinkActualStateAsync(
                    current.Id, completedState, null, actor, cancellationToken) ?? current;
                Publish(completedEvent, current);
            }
            if (changedFact && actor == "system:reconcile")
            {
                Publish(expectedConnected ? "link.reapplied" : "link.orphan-removed", current);
            }
        }
        catch (MeshFirewallUnavailableException)
        {
            current = await store.SetLinkActualStateAsync(
                current.Id, failureState, FirewallUnavailableCode, actor, cancellationToken) ?? current;
            if (actor != "system:reconcile")
            {
                Publish(failureEvent, current);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            current = await store.SetLinkActualStateAsync(
                current.Id, failureState, CompactError(exception), actor, cancellationToken) ?? current;
            Publish(failureEvent, current);
        }
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
            var gate = await AcquireLinkGateAsync(candidate.Id, cancellationToken);
            try
            {
                var current = await GetCurrentEffectiveAsync(candidate.Id, cancellationToken);
                if (current is not null && IsEligibleForFullReconciliation(current))
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

    private static bool IsEligibleForFullReconciliation(LinkPolicy link)
        => link.DesiredState == "Active"
            || (link.DesiredState == "Disabled" && link.ActualState != "Disabled");

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

    private async Task VerifyFactualStateAsync(
        LinkPolicy link,
        bool expectedConnected,
        CancellationToken cancellationToken)
    {
        if (await applier.IsConnectedAsync(link, cancellationToken) != expectedConnected)
        {
            throw new InvalidOperationException(
                $"Factual Link policy is {(expectedConnected ? "disabled" : "active")} after application.");
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
}

public sealed record LinkExpirationResult(int Disabled, int Failed);
public sealed record LinkFullReconciliationResult(int Examined, int Failed, bool FirewallUnavailable);
