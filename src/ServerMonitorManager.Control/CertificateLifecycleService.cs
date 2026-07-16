using System.Text.Json;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Control;

public sealed class CertificateLifecycleService(
    ControlStore store,
    ILinkPolicyApplier applier,
    ControlEventBroker events)
{
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(10);

    public async Task<CertificateReenrollmentTicket?> ReenrollAgentAsync(
        string nodeId,
        CertificateReenrollmentRequest request,
        string actor,
        CancellationToken cancellationToken)
    {
        var mutation = await store.BeginAgentReenrollmentAsync(
            nodeId, request, actor, TicketLifetime, cancellationToken);
        if (mutation is null)
        {
            return null;
        }

        if (mutation.IsReplay)
        {
            return mutation.Ticket;
        }

        PublishCertificate("agent.revoked", mutation.Ticket);
        foreach (var pendingLink in mutation.Links)
        {
            PublishLink("link.disconnecting", pendingLink);
            LinkPolicy actual;
            try
            {
                await applier.ApplyDisconnectAsync(pendingLink, cancellationToken);
                actual = await store.SetLinkActualStateAsync(
                        pendingLink.Id, "Disabled", null, actor, cancellationToken)
                    ?? pendingLink;
                PublishLink("link.disabled", actual);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                actual = await store.SetLinkActualStateAsync(
                        pendingLink.Id,
                        "Partial",
                        CompactError(exception),
                        actor,
                        cancellationToken)
                    ?? pendingLink;
                PublishLink("link.partial", actual);
            }
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

    private void PublishLink(string type, LinkPolicy link)
        => events.Publish(
            type,
            link.Id,
            JsonSerializer.Serialize(link, SmmJsonContext.Default.LinkPolicy));

    private static string CompactError(Exception exception)
        => exception.Message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "Policy application failed.";
}
