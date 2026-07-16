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

    public async Task<LinkPolicy> CreateAsync(
        LinkPolicyCreateRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        var mutation = await store.CreateLinkMutationAsync(request, actor, cancellationToken);
        var link = mutation.Link;
        if (mutation.IsReplay)
        {
            return link;
        }
        Publish("link.connecting", link);
        try
        {
            await applier.ApplyConnectAsync(link, cancellationToken);
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

    public async Task<LinkPolicy?> DisableAsync(
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
        if (mutation.IsReplay)
        {
            return link;
        }
        Publish("link.disconnecting", link);
        try
        {
            await applier.ApplyDisconnectAsync(link, cancellationToken);
            link = await store.SetLinkActualStateAsync(id, "Disabled", null, actor, cancellationToken) ?? link;
            Publish("link.disabled", link);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            link = await store.SetLinkActualStateAsync(
                    id, "Partial", CompactError(exception), actor, cancellationToken)
                ?? link;
            Publish("link.partial", link);
        }
        return link;
    }

    public async Task<LinkReconciliationResult> ReconcileDisabledLinksForNodeAsync(
        string nodeId,
        CancellationToken cancellationToken)
    {
        var reconciled = 0;
        var failed = 0;
        var links = await store.ListEffectiveLinksForNodeAsync(nodeId, cancellationToken);
        foreach (var candidate in links.Where(link => link.DesiredState == "Disabled"))
        {
            var gate = _reconciliationLocks.GetOrAdd(candidate.Id, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                var current = (await store.ListEffectiveLinksForNodeAsync(nodeId, cancellationToken))
                    .SingleOrDefault(link => link.Id == candidate.Id && link.DesiredState == "Disabled");
                if (current is null)
                {
                    continue;
                }

                var actor = $"system:reconnect:{nodeId}";
                var link = await store.SetLinkActualStateAsync(
                    current.Id, "Disconnecting", null, actor, cancellationToken) ?? current;
                Publish("link.reconciling", link);
                try
                {
                    await applier.ApplyDisconnectAsync(link, cancellationToken);
                    link = await store.SetLinkActualStateAsync(
                        link.Id, "Disabled", null, actor, cancellationToken) ?? link;
                    Publish("link.disabled", link);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    link = await store.SetLinkActualStateAsync(
                        link.Id, "Partial", CompactError(exception), actor, cancellationToken) ?? link;
                    Publish("link.partial", link);
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

    private void Publish(string type, LinkPolicy link)
        => events.Publish(
            type,
            link.Id,
            JsonSerializer.Serialize(link, SmmJsonContext.Default.LinkPolicy));

    private static string CompactError(Exception exception)
        => exception.Message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "Policy application failed.";
}
