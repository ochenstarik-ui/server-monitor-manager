using System.Collections.Concurrent;
using System.Text.Json;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Control;

public sealed class LinkService(
    ControlStore store,
    ILinkPolicyApplier applier,
    ControlEventBroker events)
{
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
        var gate = _reconciliationLocks.GetOrAdd(link.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = (await store.ListLinksAsync(cancellationToken))
                .SingleOrDefault(candidate => candidate.Id == link.Id)
                ?? throw new InvalidOperationException("The persisted Link disappeared.");
            if (current.DesiredState != "Active")
            {
                return current;
            }
            link = current;
            Publish("link.connecting", link);
            try
            {
                await applier.ApplyConnectAsync(link, cancellationToken);
                await VerifyFactualStateAsync(link, expectedConnected: true, cancellationToken);
                link = await store.SetLinkActualStateAsync(link.Id, "Active", null, actor, cancellationToken)
                    ?? throw new InvalidOperationException("The persisted Link disappeared.");
                Publish("link.active", link);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                link = await store.SetLinkActualStateAsync(
                        link.Id, "Failed", CompactError(exception), actor, cancellationToken)
                    ?? link;
                Publish("link.failed", link);
            }
            return link;
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
        var gate = _reconciliationLocks.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
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
        return await ConvergeDisabledCoreAsync(link, actor, cancellationToken);
    }

    internal async Task<LinkPolicy> ConvergeDisabledAsync(
        LinkPolicy link,
        string actor,
        CancellationToken cancellationToken)
    {
        var gate = _reconciliationLocks.GetOrAdd(link.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await ConvergeDisabledCoreAsync(link, actor, cancellationToken);
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
            var gate = _reconciliationLocks.GetOrAdd(candidate.Id, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                var current = (await store.ListEffectiveLinksForNodeAsync(nodeId, cancellationToken))
                    .SingleOrDefault(link => link.Id == candidate.Id);
                if (current is null)
                {
                    continue;
                }

                var actor = $"system:reconnect:{nodeId}";
                var expectedConnected = current.DesiredState == "Active";
                var pendingState = expectedConnected ? "Connecting" : "Disconnecting";
                var completedState = expectedConnected ? "Active" : "Disabled";
                var completedEvent = expectedConnected ? "link.active" : "link.disabled";
                var failureState = expectedConnected ? "Failed" : "Partial";
                var failureEvent = expectedConnected ? "link.failed" : "link.partial";
                var link = await store.SetLinkActualStateAsync(
                    current.Id, pendingState, null, actor, cancellationToken) ?? current;
                Publish("link.reconciling", link);
                try
                {
                    var isConnected = await applier.IsConnectedAsync(link, cancellationToken);
                    if (isConnected != expectedConnected)
                    {
                        if (expectedConnected)
                        {
                            await applier.ApplyConnectAsync(link, cancellationToken);
                        }
                        else
                        {
                            await applier.ApplyDisconnectAsync(link, cancellationToken);
                        }
                        await VerifyFactualStateAsync(link, expectedConnected, cancellationToken);
                    }
                    link = await store.SetLinkActualStateAsync(
                        link.Id, completedState, null, actor, cancellationToken) ?? link;
                    Publish(completedEvent, link);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    link = await store.SetLinkActualStateAsync(
                        link.Id, failureState, CompactError(exception), actor, cancellationToken) ?? link;
                    Publish(failureEvent, link);
                    failed++;
                }
                reconciled++;
            }
            finally
            {
                gate.Release();
            }
        }
        return new LinkReconciliationResult(reconciled, failed);
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
            var gate = _reconciliationLocks.GetOrAdd(candidate.Id, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                var current = (await store.ListExpiredLinksAsync(now, cancellationToken))
                    .SingleOrDefault(link => link.Id == candidate.Id);
                if (current is null)
                {
                    continue;
                }

                if (current.DesiredState == "Active")
                {
                    var result = await DisableCoreAsync(
                        current.Id,
                        new LinkPolicyDisableRequest(Guid.NewGuid().ToString()),
                        "system:ttl",
                        cancellationToken);
                    if (result?.ActualState == "Disabled")
                    {
                        disabled++;
                    }
                    else
                    {
                        failed++;
                    }
                    continue;
                }

                var retrying = await store.SetLinkActualStateAsync(
                    current.Id, "Disconnecting", null, "system:ttl-retry", cancellationToken) ?? current;
                Publish("link.disconnecting", retrying);
                try
                {
                    await applier.ApplyDisconnectAsync(retrying, cancellationToken);
                    await VerifyFactualStateAsync(retrying, expectedConnected: false, cancellationToken);
                    var completed = await store.SetLinkActualStateAsync(
                        retrying.Id, "Disabled", null, "system:ttl-retry", cancellationToken) ?? retrying;
                    Publish("link.disabled", completed);
                    disabled++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var partial = await store.SetLinkActualStateAsync(
                        retrying.Id, "Partial", CompactError(exception), "system:ttl-retry", cancellationToken)
                        ?? retrying;
                    Publish("link.partial", partial);
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

    private async Task<LinkPolicy> ConvergeDisabledCoreAsync(
        LinkPolicy link,
        string actor,
        CancellationToken cancellationToken)
    {
        var current = (await store.ListLinksAsync(cancellationToken))
            .SingleOrDefault(candidate => candidate.Id == link.Id) ?? link;
        if (current.DesiredState != "Disabled")
        {
            return current;
        }
        try
        {
            if (await applier.IsConnectedAsync(current, cancellationToken))
            {
                await applier.ApplyDisconnectAsync(current, cancellationToken);
                await VerifyFactualStateAsync(current, expectedConnected: false, cancellationToken);
            }
            if (current.ActualState != "Disabled" || current.LastError is not null)
            {
                current = await store.SetLinkActualStateAsync(
                    current.Id, "Disabled", null, actor, cancellationToken) ?? current;
                Publish("link.disabled", current);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            current = await store.SetLinkActualStateAsync(
                current.Id, "Partial", CompactError(exception), actor, cancellationToken) ?? current;
            Publish("link.partial", current);
        }
        return current;
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
        => exception.Message.Split([(char)13, '\n'], StringSplitOptions.RemoveEmptyEntries)
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
