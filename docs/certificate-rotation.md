# Control CA Certificate Rotation Procedure

This document defines the operational workflow for rotating the Root/Intermediate Certificate Authority (Control CA) for ServerMonitorManager.

## 1. Rationale & Policy

Control CA rotation is required in the following events:
- **Scheduled Rollover**: Periodic proactive rotation before CA expiration.
- **Key Compromise**: Suspected or confirmed private key exposure.
- **Cryptographic Upgrade**: Transitioning to stronger key algorithms or curves (e.g., ECDSA P-256).

> [!IMPORTANT]
> **Two-Person Policy Enforcement**:
> In accordance with [`docs/approval-policies.md`](file:///C:/Users/Ochenstarik/projects/smm-antigravity/docs/approval-policies.md), initiating or finalizing a Control CA rotation requires a `two_person` confirmation policy where an operator request must be confirmed by a distinct authorized reviewer before executing CA replacement in production.

---

## 2. Step-by-Step Rotation Procedure

```mermaid
sequenceDiagram
    autonumber
    participant Admin as Operator (Desktop/CLI)
    participant Hub as Control Hub
    participant Agent as Agent Node

    Note over Hub: Phase 1: Generate New CA
    Admin->>Hub: Generate new CA keypair (control-ca-next.pfx)
    Note over Hub, Agent: Phase 2: Dual-Trust Distribution
    Hub->>Agent: Distribute Combined CA Bundle (old CA + new CA)
    Note over Agent: Phase 3: Client Certificate Re-issuance
    Agent->>Hub: Submit CSR signed with active mTLS
    Hub->>Agent: Issue new client cert signed by new CA
    Agent->>Agent: Atomically replace agent.pfx (0600 permissions)
    Note over Hub: Phase 4: Old CA Retirement
    Admin->>Hub: Revoke/Archive Old CA; Enforce New CA only
```

### Phase 1: New CA Keypair Generation
Generate a new ECDSA P-256 Control CA certificate and PFX bundle:
```bash
# Generate new CA private key & cert
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 \
  -days 3650 -nodes -keyout control-ca-next.key -out control-ca-next.crt \
  -subj "/CN=ServerMonitorManager Control CA v2"

# Package into PFX format
openssl pkcs12 -export -out control-ca-next.pfx \
  -inkey control-ca-next.key -in control-ca-next.crt -passout pass:
```

### Phase 2: Dual-Trust Distribution
Append `control-ca-next.crt` to the active CA trust bundle on Control Hub and Agents:
- Control Hub configuration `CertificateAuthorityPath` points to dual-trust bundle.
- Agents update their `control-ca.crt` trust store to trust both old and new CA roots.

### Phase 3: Agent & Operator Certificate Re-issuance
Force client certificate renewal for all enrolled agents and operators:
1. Agent detects new CA bundle during periodic `EnsureCertificateRenewedAsync`.
2. Agent generates a new ECDSA P-256 key pair and CSR (`CN={node_id}`).
3. Agent sends CSR over existing active mTLS channel (`POST /api/v1/agents/certificate/renew`).
4. Control Hub issues new certificate signed by the new CA.
5. Agent atomically writes new PFX to `agent.pfx.tmp`, sets `0600` permissions (`UnixFileMode.UserRead | UnixFileMode.UserWrite`), and renames to `agent.pfx`.

### Phase 4: Retirement of Old CA
Once all agents have successfully migrated to certificates issued by the new CA:
1. Update `ControlOptions:CertificateAuthorityPath` to point solely to `control-ca-next.pfx`.
2. Remove old CA certificate from trusted store.
3. Restart Control Hub service.

---

## 3. Verification & Compliance Commands

### Verify Active Agent Certificate Issuer Distribution
Query Control API `/api/v1/control/agents` to verify zero agents remain on old CA certificates:
```bash
curl -k --cert device.pfx --cert-type PFX https://control.smm.local/api/v1/control/agents \
  | jq '.[] | {node_id: .nodeId, remaining_days: .certificateRemainingDays, expires_at: .certificateExpiresAt}'
```

### Audit Log Inspection
Verify audit events for CA rotation and certificate renewals:
```bash
sqlite3 /var/lib/ochenstarik-server-monitor-manager/control.db \
  "SELECT timestamp, actor_id, action_type, entity_id FROM audit WHERE action_type LIKE '%certificate%' ORDER BY timestamp DESC LIMIT 20;"
```
