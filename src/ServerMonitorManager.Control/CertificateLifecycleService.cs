using System.Text.Json;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Control;

public sealed class CertificateLifecycleService(
    ControlStore store,
    LinkService links,
    ControlEventBroker events,
    CertificateAuthority? ca = null)
{
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(10);

    public async Task<IssuedCertificate> RenewAgentCertificateAsync(
        string nodeId,
        CertificateRenewalRequest request,
        string authenticatedNodeId,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(nodeId, authenticatedNodeId, StringComparison.Ordinal)
            || !string.Equals(request.EntityId, authenticatedNodeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Node ID mismatch for certificate renewal.");
        }

        var isRevoked = await store.IsAgentRevokedAsync(nodeId, cancellationToken);
        if (isRevoked)
        {
            throw new InvalidOperationException("Revoked agent cannot renew certificate.");
        }

        if (ca is null)
        {
            throw new InvalidOperationException("Certificate authority is not configured.");
        }
        var issued = ca.IssueClientCertificate(nodeId, request.CertificateSigningRequestPem);
        await store.UpdateAgentCertificateAsync(nodeId, issued.Thumbprint, issued.ExpiresAt, cancellationToken);

        events.Publish(
            "certificate.renewed",
            nodeId,
            JsonSerializer.Serialize(
                new CertificateStatusEvent("Agent", nodeId, "Renewed", 0),
                SmmJsonContext.Default.CertificateStatusEvent));

        return issued;
    }

    public async Task<IssuedCertificate> RenewDeviceCertificateAsync(
        string deviceId,
        CertificateRenewalRequest request,
        string authenticatedDeviceId,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(deviceId, authenticatedDeviceId, StringComparison.Ordinal)
            || !string.Equals(request.EntityId, authenticatedDeviceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Device ID mismatch for certificate renewal.");
        }

        var isRevoked = await store.IsDeviceRevokedAsync(deviceId, cancellationToken);
        if (isRevoked)
        {
            throw new InvalidOperationException("Revoked device cannot renew certificate.");
        }

        if (ca is null)
        {
            throw new InvalidOperationException("Certificate authority is not configured.");
        }
        var issued = ca.IssueClientCertificate(deviceId, request.CertificateSigningRequestPem);
        await store.UpdateDeviceCertificateAsync(deviceId, issued.Thumbprint, issued.ExpiresAt, cancellationToken);

        events.Publish(
            "certificate.renewed",
            deviceId,
            JsonSerializer.Serialize(
                new CertificateStatusEvent("Operator", deviceId, "Renewed", 0),
                SmmJsonContext.Default.CertificateStatusEvent));

        return issued;
    }

    public async Task CheckAndPublishExpiringCertificatesAsync(CancellationToken cancellationToken = default)
    {
        var agents = await store.ListAgentsAsync(cancellationToken);
        foreach (var agent in agents)
        {
            if (agent.CertificateExpiresAt.HasValue)
            {
                var remaining = agent.CertificateExpiresAt.Value - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero && remaining.TotalDays < 10)
                {
                    events.Publish(
                        "certificate.expiring",
                        agent.NodeId,
                        JsonSerializer.Serialize(
                            new CertificateStatusEvent("Agent", agent.NodeId, "Expiring", 0),
                            SmmJsonContext.Default.CertificateStatusEvent));
                }
            }
        }
    }

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
