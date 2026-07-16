using System.Text.Json;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Control;

public sealed class LinkService(
    ControlStore store,
    ILinkPolicyApplier applier,
    ControlEventBroker events)
{
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

    private void Publish(string type, LinkPolicy link)
        => events.Publish(
            type,
            link.Id,
            JsonSerializer.Serialize(link, SmmJsonContext.Default.LinkPolicy));

    private static string CompactError(Exception exception)
        => exception.Message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "Policy application failed.";
}
