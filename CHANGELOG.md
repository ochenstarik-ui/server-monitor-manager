# Changelog

All notable changes to Server Monitor Manager are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions follow the tags in this repository.

## [Unreleased]

### Added
- Link management and a real-time Event Journal in the Control web console (#63)

### Changed
- Task, report, and acceptance workflow for agents moved into `agents/` (#64)
- `actions/download-artifact` pinned to v8.0.1 across all workflows (#46)
- `docker/setup-qemu-action` pinned to v4.2.0 (#44)

---

## [v0.1.0-alpha.20] — 2026-08-18

Published. Carries the work that `v0.1.0-alpha.19` failed to publish.

### Added
- Release pipeline now tests the latest published release before cutting a new
  one (#62)
- Operator web console for nodes, links, and enrollment code generation (#59)
- Guided unified installer (#58)
- Complete server uninstall mode (#60)
- Testing-only password login for the web console (#61)
- Node enrollment endpoint (#53)
- Desktop update proof (#55)

### Fixed
- Mesh state directory permissions created before Control starts (#57)
- `DEFAULT_RELEASE_TAG` in the packaged convenience installer matches the tag
  being produced — the defect that burned `v0.1.0-alpha.19`

---

## [v0.1.0-alpha.19] — 2026-08-18 — burned, not published

The tag exists, but the Release pipeline failed before a GitHub Release was
published: the packaged convenience installer's `DEFAULT_RELEASE_TAG` did not
match the immutable tag. The version number is burned and must not be moved,
deleted, recreated, or reused. Its content was published under
`v0.1.0-alpha.20`.

---

## [v0.1.0-alpha.18] — 2026-08-18

### Fixed
- Wrong-identity negative test selects cosign v3 explicit detached-output mode,
  matching the production consumer's detached signature and certificate
  contract (#56)

First release required to complete both automatic `workflow_run` verification
and manual `workflow_dispatch` re-verification.

---

## [v0.1.0-alpha.17] — 2026-08-18

### Fixed
- Checksum verification stays fail-closed while its success line is suppressed,
  so pass-through commands such as `node-code` return only machine-readable
  bootstrap output (#54)

Automatic verification completed clean-host Hub and Node installation, then the
negative-test harness stopped while creating a wrong-identity signature:
cosign v3 requires an explicit bundle or legacy detached-output mode.

---

## [v0.1.0-alpha.16] — 2026-08-17

### Fixed
- Acceptance shell clears its command hash after removing test-provisioned
  cosign (#52)

Automatic verification proved clean-host Hub installation, then failed on the
Node: the convenience installer wrote the checksum success line to stdout ahead
of the machine-readable `SMMNODE2` enrollment code, so the value was rejected.

---

## [v0.1.0-alpha.15] — 2026-08-17

### Added
- Pinned, checksum-verified cosign binary provisioned by the installer (#43)

First release that provisions cosign. Its verification proved clean-host Hub
installation and manifest verification, then stopped before the clean-host Node
installation: the acceptance script retained the deliberately removed cosign
path in Bash's command hash.

---

## [v0.1.0-alpha.14] — 2026-08-15

### Fixed
- Release signing consistency between producer and consumer (#41, #42)

First release with the complete manifest, keyless signature, and Fulcio
certificate set, so its published assets can be verified. A clean host cannot
install it because the release does not provision cosign — preserve it for
verification and historical evidence, do not use it for installation.

---

## [v0.1.0-alpha.13] — 2026-08-13

### Fixed
- Windows checksum asset normalized to LF so GNU `sha256sum -c` can consume it
  (#39)

Published with a keyless manifest signature but without the Fulcio signing
certificate required by production consumers. The immutable release remains
published as historical evidence.

---

## [v0.1.0-alpha.12] — 2026-08-13

### Fixed
- Network-dependent alpha.8 compatibility test replaced in release
  verification (#38)

Published with a keyless manifest signature but without the Fulcio signing
certificate, so consumers cannot verify that signature. Its Windows
`SHA256SUMS` asset also used CRLF and was not consumable by GNU
`sha256sum -c`. Neither defect is repaired in place.

---

## [v0.1.0-alpha.11] — 2026-08-12 — burned, not published

The tag exists, but the Release pipeline failed before a GitHub Release was
published. The version number is burned and must not be moved, deleted,
recreated, or reused.

Content carried by the tag: release contract updated to alpha.10, backward
compatibility test removed from the bootstrap job, Debian VM reboot constraint
documented, translations and roadmap synchronized.

---

## [v0.1.0-alpha.10] — 2026-08-11 — burned, not published

The tag exists, but the Release pipeline failed before a GitHub Release was
published. The version number is burned and must not be moved, deleted,
recreated, or reused.

Content carried by the tag: signed delivery queue B (#35), version comparison
fix (#36).

---

## [v0.1.0-alpha.9] — 2026-08-10

### Added
- Monitor snapshot contract (#32)

### Changed
- Single-writer release pipeline: `linux-release.yml` is the sole GitHub
  Release publisher (#33)

`smm-setup.sh`, its checksum, the bootstrap script, platform archives, SBOMs,
and the signed manifest are reproducible from the tagged tree. The installer
fetches only same-tag assets and verifies the bootstrap checksum before
execution.

---

## [v0.1.0-alpha.8] — 2026-08-10

### Added
- Source-scoped monitor role (#29)
- Certificate lifecycle management
- Signed delivery, queue A
- Reproducible builds (#15)
- Product horizons and KAgent integration specifications (#13)

### Fixed
- CycloneDX SBOM generation in release jobs (#28)
- Repository hygiene (#14)

Contains the v1 `server-monitor-manager-bootstrap-manifest.json` layout and an
orphaned `server-monitor-manager-manifest.sig` without the corresponding
manifest v2. Published assets remain immutable; the anomaly is documented
rather than repaired in place.

---

## [v0.1.0-alpha.7] — 2026-08-09

### Added
- Background link reconciliation service for the Control plane (#12)
- Link reconciliation driven by factual state (#16)
- Provisioning helper coverage for a missing node row (#17)

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

[Unreleased]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.20...HEAD
[v0.1.0-alpha.20]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.19...v0.1.0-alpha.20
[v0.1.0-alpha.19]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.18...v0.1.0-alpha.19
[v0.1.0-alpha.18]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.17...v0.1.0-alpha.18
[v0.1.0-alpha.17]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.16...v0.1.0-alpha.17
[v0.1.0-alpha.16]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.15...v0.1.0-alpha.16
[v0.1.0-alpha.15]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.14...v0.1.0-alpha.15
[v0.1.0-alpha.14]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.13...v0.1.0-alpha.14
[v0.1.0-alpha.13]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.12...v0.1.0-alpha.13
[v0.1.0-alpha.12]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.11...v0.1.0-alpha.12
[v0.1.0-alpha.11]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.10...v0.1.0-alpha.11
[v0.1.0-alpha.10]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.9...v0.1.0-alpha.10
[v0.1.0-alpha.9]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.8...v0.1.0-alpha.9
[v0.1.0-alpha.8]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.7...v0.1.0-alpha.8
[v0.1.0-alpha.7]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.6...v0.1.0-alpha.7
[v0.1.0-alpha.6]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.5...v0.1.0-alpha.6
[v0.1.0-alpha.5]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.4...v0.1.0-alpha.5
[v0.1.0-alpha.4]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.3...v0.1.0-alpha.4
[v0.1.0-alpha.3]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.2...v0.1.0-alpha.3
[v0.1.0-alpha.2]: https://github.com/ochenstarik-ui/server-monitor-manager/compare/v0.1.0-alpha.1...v0.1.0-alpha.2
[v0.1.0-alpha.1]: https://github.com/ochenstarik-ui/server-monitor-manager/releases/tag/v0.1.0-alpha.1
