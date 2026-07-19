# Server Monitor Manager

**English** · [Русский](docs/i18n/README.ru.md) · [Español](docs/i18n/README.es.md) · [简体中文](docs/i18n/README.zh-CN.md) · [हिन्दी](docs/i18n/README.hi.md) · [العربية](docs/i18n/README.ar.md) · [Português](docs/i18n/README.pt-BR.md) · [Français](docs/i18n/README.fr.md) · [Deutsch](docs/i18n/README.de.md) · [日本語](docs/i18n/README.ja.md) · [한국어](docs/i18n/README.ko.md) · [Türkçe](docs/i18n/README.tr.md)

Server Monitor Manager is a lightweight, Windows-first application for monitoring Linux servers, opening direct SSH sessions, and explicitly controlling secure connections between servers. It is designed for personal infrastructure and small fleets where a heavy monitoring platform, Kubernetes, or a public API on every node would be unnecessary.

The current alpha combines a packaged WinUI 3 desktop client, an ASP.NET Core control service, a small outbound Linux agent, SQLite storage, and a WireGuard data plane managed by restrictive nftables policies.

## What it does

- monitors CPU/load, memory, swap, disks, inodes, network activity, uptime, latency, SSH, and WireGuard;
- keeps several server profiles, local health warnings, and short metric history;
- generates a dedicated Ed25519 SSH key and stores private material only on the Windows device;
- opens direct SSH terminals without sending a private terminal key to the Hub;
- exports support diagnostics with hashed endpoint identities and without hosts, users, keys, certificates, or tokens;
- joins servers through one Hub with a public IP; secondary servers need outbound access only;
- creates directional Links such as `AI agent → Home server:22` and disables each Link independently;
- limits Links by source, destination `/32`, TCP/UDP port, policy version, and optional TTL;
- uses one-time enrollment tokens, CSR-based certificates, mTLS, role separation, idempotency, and audit records;
- runs without Docker or a database on every Node.

## Architecture

```text
Windows desktop -- mTLS/HTTPS --> Control Hub (ASP.NET Core + SQLite)
       |                              |
       +-------- direct SSH ----------+
                                      |
                            WireGuard + nftables
                              /       |       \
                      AI-agent     Home      Server 2
```

The Hub has a public IP and coordinates the fleet. Nodes initiate their own WireGuard and mTLS connections, so a home server behind NAT does not need a white/dedicated IP or an inbound public port. Transit is denied by default. A Link is directional: enabling `AI-agent → Home` does not enable `Home → AI-agent` or access to another server.

The control plane and data plane are separated:

- **Control plane:** ASP.NET Core 10, SQLite inventory, metrics, policies, history, audit, enrollment, and an authenticated event stream.
- **Data plane:** WireGuard peers and persistent nftables ACLs on the Hub.
- **Desktop:** packaged WinUI 3 client with DPAPI-protected operator certificate and SSH identity.
- **Agent:** self-contained Linux binary for `amd64` and `arm64`; it only creates outbound mTLS sessions.

See [architecture](docs/architecture.md), [security model](docs/security-model.md), [roadmap](docs/roadmap.md), [Linux bootstrap contract](docs/installer-contract.md), and the [Provisioning and Xray specification](docs/provisioning-vpn-requirements.md).

Operational procedures are documented in [Linux bootstrap](docs/linux-bootstrap.md), [Control backup and recovery](docs/control-backup.md), and the [three-server acceptance test](docs/three-server-acceptance.md).

## Repository layout

```text
src/ServerMonitorManager.Desktop/  Windows WinUI 3 client
src/ServerMonitorManager.Core/     Shared contracts and models
src/ServerMonitorManager.Control/  Hub API, SQLite, events, and policy coordination
src/ServerMonitorManager.Agent/    Outbound Linux monitoring agent
tests/                              Control-plane tests
docs/                               Architecture, security, roadmap, translations
```

Self-contained Control and Agent binaries are published with [Server Monitor Manager releases](https://github.com/ochenstarik-ui/server-monitor-manager/releases). The project-owned Linux bootstrap is specified but is not yet included in the current alpha release. Until it is implemented, checksummed, and tested, the project does not publish a one-command Hub/Node installation instruction.

The planned bootstrap, helper, schemas, and compatibility manifest will be maintained and released only from this repository. See the [Linux bootstrap contract](docs/installer-contract.md). Do not use an installer from another project as a Server Monitor Manager component.

The `SMMDEV1` flow enrolls the Windows application: the app creates its operator key locally, confirms the Hub CA fingerprint, obtains a separate certificate, and protects it with Windows DPAPI.

An Operator can issue a ten-minute Automation enrollment token for exactly one source Node through `POST /api/v1/control/automations/token` or the local `automation-token-create AUTOMATION_ID SOURCE_NODE_ID` command. The automation process creates its private key and CSR locally, enrolls through `/api/v1/automation-enroll`, and can then read only `/api/v1/automation/links`. Link mutations remain Operator-only.

## Windows client

Requirements for building from source:

- Windows 10 version 1809 or later / Windows 11;
- .NET 10 SDK;
- Visual Studio 2022 with Windows App SDK tooling, or compatible CLI workloads;
- system OpenSSH client.

```powershell
dotnet build ServerMonitorManager.slnx --configuration Release
dotnet test tests/ServerMonitorManager.Control.Tests/ServerMonitorManager.Control.Tests.csproj --configuration Release
```

Windows CI also produces a test-signed x64 MSIX with a public test certificate and `SHA256SUMS`. See [Windows installer documentation](docs/windows-installer.md). Public releases use a trusted PFX when the signing secrets are configured; otherwise the artifact is explicitly test-signed.

In the application, generate or copy the monitoring SSH key, add the Hub profile, mark it as the Mesh Hub, and use **Control Hub** to paste the `SMMDEV1` code. The Mesh view then reads inventory and Links from the authenticated Control API and receives live Link/heartbeat events.

## Security model

- no shared root password and no Node private WireGuard key on the Hub;
- separate monitoring, terminal, Agent, Operator, and AI-automation identities;
- SSH monitoring uses a root-owned forced command without shell, PTY, or forwarding;
- Agent certificates can only submit heartbeat data for their own Node;
- Operator certificates are required for inventory, Links, and event streaming;
- Automation certificates are bound to one source Node and can only read that source's effective Link grants; they cannot create, disable, or enumerate unrelated Links;
- Link traffic is denied by default and allowed only by explicit nftables rules;
- disabling a Link persists the desired state before the firewall rule is removed;
- idempotency keys prevent a retry from repeating a policy side effect;
- secrets and production configuration must never be committed to Git.

## Current status

`v0.1.0-alpha.5` is an early testing release, not a production security appliance. Windows and Linux builds, control-plane tests, a test-signed x64 MSIX, self-contained `linux-x64`/`linux-arm64` artifacts, and SHA-256 checksums are automated in GitHub Actions.

The current development branch implements dedicated Windows pages for Servers, Links, Sessions, and Settings; SSH monitoring; directional Links; one-time enrollment; separate mTLS Agent, Operator, and source-scoped Automation identities; certificate revocation/re-enrollment; SQLite control state; audit; authenticated event streaming; Windows Control API integration; and a bounded durable Agent buffer with downsampling.

Reconnect reconciliation is implemented with a durable SQLite marker: after a Node returns, the Hub reapplies the latest effective disabled policies and clears the marker only after the firewall confirms success. Control also expires TTL Links through the firewall helper, prunes bounded operational data, versions its SQLite schema, and creates verified backups of SQLite state and the Control CA. The Provisioning control plane persists versioned jobs, enforces TTL/audit/idempotency and one active job per Node, and supports confirmation, progress, reconciliation, retry, and rollback states. A restricted root helper now accepts only versioned, module-hashed allowlisted requests through a local Unix socket; the first executable action is read-only Linux `preflight`. CI exercises process boundaries, authorization, Agent parsing, Desktop contracts, and concurrent heartbeat/replay. Still required are mutating Provisioning actions with factual-state verification, physical WireGuard/nftables/reboot acceptance, trusted public code signing, Xray, and clients for additional platforms.

## License and project policy

Copyright 2026 ochenstarik-ui. Server Monitor Manager is licensed under the [Apache License 2.0](LICENSE), including its explicit patent grant and redistribution conditions.

The project is under active alpha development. Review scripts and release checksums before testing, use disposable or backed-up servers, and do not expose the Control port without firewall restrictions.
