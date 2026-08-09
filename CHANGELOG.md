# Changelog

All notable changes to Server Monitor Manager are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions follow the tags in this repository.

## [Unreleased]

### Added
- Background link reconciliation service for the Control plane (#12)

### Changed
- Link policy reconciliation now runs continuously (#11)

### Fixed
- Closed security debts in the Desktop app and provisioning helper (#10)

---

## [v0.1.0-alpha.6] — 2026-07-31

### Added
- Standalone Linux bootstrap foundation: one-command server installation
  script (`deploy/ochenstarik-server-monitor-manager.sh`) (#5)
- Standalone Server Monitor Manager roadmap (`docs/roadmap.md`) (#5)
- Confirmed timezone provisioning executed safely (#6)
- Hardened enrollment and provisioning helper (#7)
- SSH trust pinning and session key protection (#8)

### Changed
- MSIX version bumped to 1.0.0.6 (#9)

---

## [v0.1.0-alpha.5] — 2026-07-17

### Added
- Signed Windows MSIX release pipeline
- Dedicated desktop management pages
- 100-node Hub load test
- Source-scoped automation identity
- Kill switch helper failure tests
- Export of redacted desktop diagnostics

### Fixed
- Diagnostics JSON made trim-safe
- Disabled links enforced after reconnect
- MSIX publishing fixed on clean runners
- Windows workflow script indentation normalized

---

## [v0.1.0-alpha.4] — 2026-07-17

### Added
- Certificate re-enrollment lifecycle

---

## [v0.1.0-alpha.3] — 2026-07-16

### Added
- Apache 2.0 license
- Offline agent metrics buffering
- Project documentation in twelve languages

---

## [v0.1.0-alpha.2] — 2026-07-16

### Changed
- Links migrated to SQLite control plane

---

## [v0.1.0-alpha.1] — 2026-07-16

### Added
- Initial repository with persistent mTLS control layer
- Windows SSH monitoring MVP
- One-command server installation foundation
- Directed server mesh controls
- Server profile editing and deletion
- Restricted Link policy controls
- Confirmed applied Link state in Windows client
- Health warnings and automatic refresh
- Charted short metrics history
- Application icon assets
- Secure node enrollment documentation
- Windows build verification in CI

[Unreleased]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.6...HEAD
[v0.1.0-alpha.6]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.5...v0.1.0-alpha.6
[v0.1.0-alpha.5]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.4...v0.1.0-alpha.5
[v0.1.0-alpha.4]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.3...v0.1.0-alpha.4
[v0.1.0-alpha.3]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.2...v0.1.0-alpha.3
[v0.1.0-alpha.2]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.1...v0.1.0-alpha.2
[v0.1.0-alpha.1]: https://github.com/ochenstarik-ui/server-monitor-manager/releases/tag/v0.1.0-alpha.1
