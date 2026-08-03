using System.Text.Json;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Control;

public sealed class CertificateLifecycleService(
    ControlStore store,
    LinkService links,
    ControlEventBroker events)
{
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(10);

    public async Task<CertificateReenrollmentTicket?> ReenrollAgentAsync(
        string nodeId,
        CertificateReenrollmentRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        using var nodeLease = await links.AcquireNodeLocksAsync([nodeId], cancellationToken);
        var mutation = await store.BeginAgentReenrollmentAsync(
            nodeId, request, actor, TicketLifetime, cancellationToken);
        if (mutation is null)
        {
            return null;
        }

        if (!mutation.IsReplay)
        {
            PublishCertificate("agent.revoked", mutation.Ticket);
        }
        var pendingLinks = mutation.IsReplay
            ? (await store.ListEffectiveLinksForNodeAsync(nodeId, cancellationToken))
                .Where(link => link.DesiredState == "Disabled")
            : mutation.Links;
        foreach (var pendingLink in pendingLinks)
        {
            await links.ConvergeDisabledAsync(pendingLink, actor, cancellationToken);
        }

        return mutation.Ticket;
    }

    public async Task<CertificateReenrollmentTicket?> ReenrollDeviceAsync(
        string deviceId,
        CertificateReenrollmentRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        var ticket = await store.BeginDeviceReenrollmentAsync(
            deviceId, request, actor, TicketLifetime, cancellationToken);
        if (ticket is not null)
        {
            PublishCertificate("device.revoked", ticket);
        }

        return ticket;
    }

    private void PublishCertificate(string type, CertificateReenrollmentTicket ticket)
        => events.Publish(
            type,
            ticket.EntityId,
            JsonSerializer.Serialize(
                new CertificateStatusEvent(
                    ticket.EntityType,
                    ticket.EntityId,
                    "Revoked",
                    ticket.DisabledLinks),
                SmmJsonContext.Default.CertificateStatusEvent));

}
