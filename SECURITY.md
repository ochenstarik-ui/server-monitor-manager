# Security Policy

## Supported Versions

Server Monitor Manager is currently in alpha. Only the latest pre-release is
supported with security fixes:

| Version | Supported |
|---|---|
| latest alpha (v0.1.0-alpha.6) | ✅ |
| earlier alphas | ❌ |

## Reporting a Vulnerability

**Do not open a public GitHub issue for security vulnerabilities.**

Report vulnerabilities privately via GitHub's built-in mechanism:
[Security → Report a vulnerability](https://github.com/ochenstarik-ui/server-monitor-manager/security/advisories/new)

This opens a private advisory draft visible only to repository maintainers.

### What to include

- A description of the vulnerability and its potential impact.
- Steps to reproduce or a proof-of-concept (even a minimal one).
- The version or commit you tested against.
- Your GitHub handle or email if you want to be credited.

### Response timeline

| Milestone | Target |
|---|---|
| Initial acknowledgement | Within **72 hours** of receipt |
| Triage and severity assessment | Within **7 days** |
| Patch or mitigation plan | Communicated within **14 days** |
| Public disclosure | Coordinated with the reporter |

## Threat Model and Scope

Server Monitor Manager installs binaries that run as **root** on servers and
manages firewall rules. The following are considered **in-scope
vulnerabilities**:

- **Role separation bypass** — an Agent being able to perform Control
  operations or vice versa without explicit provisioning.
- **Unauthorized root execution** — obtaining root-level code execution
  outside of the typed provisioning flow (`install-control`,
  `install-agent`).
- **Private key or enrollment token leakage** — exposure of mTLS private
  keys, CA keys, or enrollment tokens to unprivileged processes or logs.
- **Kill switch bypass** — circumventing the emergency kill switch
  (`ochenstarik-smm-emergency`) or the disabled-link enforcement.
- **Supply chain / artifact substitution** — an attacker substituting
  release artifacts or bootstrap scripts to deliver malicious binaries.

## Known Limitations (Not Vulnerabilities)

The following are **documented alpha limitations** and will not be treated as
security vulnerabilities until addressed in the roadmap:

- **Release manifest is not cryptographically signed.** The bootstrap manifest
  (`server-monitor-manager-bootstrap-manifest.json`) includes a SHA-256
  checksum but the manifest itself carries no signature. Tracked in
  [`docs/roadmap.md`](docs/roadmap.md).
- **Windows MSIX is not trusted-signed.** The Windows installer is signed with
  a test or self-signed certificate in CI. Trust requires a commercial code
  signing certificate. Tracked in [`docs/roadmap.md`](docs/roadmap.md).

Both items are openly acknowledged limitations of the alpha stage. Reports
about these specific issues will be noted but not assigned a CVE or priority
fix until the roadmap items are scheduled.

## Security Model Reference

For a complete description of the trust boundaries, role separation, and
threat model see [`docs/security-model.md`](docs/security-model.md).
