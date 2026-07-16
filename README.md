# Server Monitor Manager

**English** · [Русский](docs/i18n/README.ru.md) · [Español](docs/i18n/README.es.md) · [简体中文](docs/i18n/README.zh-CN.md) · [हिन्दी](docs/i18n/README.hi.md) · [العربية](docs/i18n/README.ar.md) · [Português](docs/i18n/README.pt-BR.md) · [Français](docs/i18n/README.fr.md) · [Deutsch](docs/i18n/README.de.md) · [日本語](docs/i18n/README.ja.md) · [한국어](docs/i18n/README.ko.md) · [Türkçe](docs/i18n/README.tr.md)

Server Monitor Manager is a lightweight, Windows-first application for monitoring Linux servers, opening direct SSH sessions, and explicitly controlling secure connections between servers. It is designed for personal infrastructure and small fleets where a heavy monitoring platform, Kubernetes, or a public API on every node would be unnecessary.

The current alpha combines a packaged WinUI 3 desktop client, an ASP.NET Core control service, a small outbound Linux agent, SQLite storage, and a WireGuard data plane managed by restrictive nftables policies.

## What it does

- monitors CPU/load, memory, swap, disks, inodes, network activity, uptime, latency, SSH, and WireGuard;
- keeps several server profiles, groups, tags, favorites, alerts, and short local metric history;
- generates a dedicated Ed25519 SSH key and stores private material only on the Windows device;
- opens direct SSH terminals without sending a private terminal key to the Hub;
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

See [architecture](docs/architecture.md), [security model](docs/security-model.md), [roadmap](docs/roadmap.md), and [installer contract](docs/installer-contract.md).

## Repository layout

```text
src/ServerMonitorManager.Desktop/  Windows WinUI 3 client
src/ServerMonitorManager.Core/     Shared contracts and models
src/ServerMonitorManager.Control/  Hub API, SQLite, events, and policy coordination
src/ServerMonitorManager.Agent/    Outbound Linux monitoring agent
tests/                              Control-plane tests
docs/                               Architecture, security, roadmap, translations
```

The Linux installer is maintained in [`ochenstarik-ui/lightweight-server`](https://github.com/ochenstarik-ui/lightweight-server) as `ochenstarik-server-monitor-manager.sh`. Release binaries are attached to [Server Monitor Manager releases](https://github.com/ochenstarik-ui/server-monitor-manager/releases).

## Quick start: Hub and two Nodes

Use a fresh Debian or Ubuntu server with a public IP as the Hub. Download and inspect the installer before running it:

```bash
curl -fLO https://raw.githubusercontent.com/ochenstarik-ui/lightweight-server/main/ochenstarik-server-monitor-manager.sh
chmod 700 ochenstarik-server-monitor-manager.sh
bash -n ochenstarik-server-monitor-manager.sh
sudo ./ochenstarik-server-monitor-manager.sh hub
```

Open the selected WireGuard UDP port (default `51820`) and Control Hub TCP port `7443`. Create enrollment codes on the Hub:

```bash
sudo ochenstarik-smm node-code home
sudo ochenstarik-smm node-code ai-agent
```

On each secondary server, use the same installer and select the Node role. Paste the code for that Node. The private WireGuard key is created locally and never leaves the Node. Then install the persistent control layer:

```bash
# Hub
sudo ./ochenstarik-server-monitor-manager.sh install-control-hub
sudo ./ochenstarik-server-monitor-manager.sh control-code home
sudo ./ochenstarik-server-monitor-manager.sh control-device-code windows-pc

# Node: paste the corresponding SMMCTL1 code when prompted
sudo ./ochenstarik-server-monitor-manager.sh install-control-agent
```

The installer selects the `amd64` or `arm64` archive and verifies its SHA-256 checksum. The `SMMDEV1` code enrolls the Windows application: the app creates its operator key locally, confirms the Hub CA fingerprint, obtains a separate certificate, and protects it with Windows DPAPI.

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

In the application, generate or copy the monitoring SSH key, add the Hub profile, mark it as the Mesh Hub, and use **Control Hub** to paste the `SMMDEV1` code. The Mesh view then reads inventory and Links from the authenticated Control API and receives live Link/heartbeat events.

## Security model

- no shared root password and no Node private WireGuard key on the Hub;
- separate monitoring, terminal, Agent, Operator, and AI-automation identities;
- SSH monitoring uses a root-owned forced command without shell, PTY, or forwarding;
- Agent certificates can only submit heartbeat data for their own Node;
- Operator certificates are required for inventory, Links, and event streaming;
- Link traffic is denied by default and allowed only by explicit nftables rules;
- disabling a Link persists the desired state before the firewall rule is removed;
- idempotency keys prevent a retry from repeating a policy side effect;
- secrets and production configuration must never be committed to Git.

## Current status

`v0.1.0-alpha.4` is an early testing release, not a production security appliance. Windows and Linux builds, control-plane tests, Bash syntax checks, self-contained `linux-x64`/`linux-arm64` artifacts, and checksums are automated in GitHub Actions.

The current development branch implements Windows SSH monitoring, the Hub/Node WireGuard installer, directional Links, one-time enrollment, mTLS Agent and Operator identities, certificate revocation/re-enrollment, SQLite control state, audit, authenticated event streaming, Windows Control API integration, and a bounded durable Agent buffer with downsampling.

Reconnect reconciliation is implemented with a durable SQLite marker: after a Node returns, the Hub reapplies the latest effective disabled policies and clears the marker only after the firewall confirms success. Linux CI exercises the real Control-to-helper process boundary, including a helper failure and Control process reconstruction over the same SQLite database. Still planned: end-to-end nftables and host-reboot tests with the installer, a 50–100 Node load test, signed Windows installer, and desktop/mobile clients for additional platforms.

## License and project policy

Copyright 2026 ochenstarik-ui. Server Monitor Manager is licensed under the [Apache License 2.0](LICENSE), including its explicit patent grant and redistribution conditions.

The project is under active alpha development. Review scripts and release checksums before testing, use disposable or backed-up servers, and do not expose the Control port without firewall restrictions.
