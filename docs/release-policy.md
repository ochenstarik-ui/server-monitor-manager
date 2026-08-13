# Release policy

Published tags and release assets are immutable.

A tag that has been published must never be moved, reused, deleted and recreated, or supplied with replacement assets under the same names. If a published build or installer is wrong, preserve the existing release and publish a new, higher version tag containing the correction.

`.github/workflows/linux-release.yml` is the sole GitHub Release publisher. On a version tag, it builds the Linux and Windows packages from the tagged commit, generates the signed manifest, and publishes the complete release asset set. `.github/workflows/windows-release.yml` is manual-only and may package and verify a Windows installer as a workflow artifact, but it never publishes or replaces GitHub Release assets. The tracked production source for the convenience installer is `deploy/smm-setup.sh`; the Linux release workflow copies that exact file to the release artifact set, records its SHA-256 in the signed manifest, and publishes its standalone checksum. The default release in that source must match the tag being produced.

For `v0.1.0-alpha.9`, this makes `smm-setup.sh`, `smm-setup.sh.sha256`, the bootstrap script, platform archives, SBOMs, and the signed manifest reproducible from the tagged tree. The installer fetches only same-tag assets and verifies the bootstrap checksum before execution. Corrections after publication require another tag; the `v0.1.0-alpha.9` tag and assets remain unchanged.

Known release history:

- `v0.1.0-alpha.8` contains the v1 `server-monitor-manager-bootstrap-manifest.json` layout and an orphaned `server-monitor-manager-manifest.sig` without the corresponding manifest v2. Published assets remain immutable; the anomaly is documented rather than repaired in place.
- Tags `v0.1.0-alpha.10` and `v0.1.0-alpha.11` exist, but their Release pipelines failed before a GitHub Release was published. Those version numbers are burned and must not be moved, deleted, recreated, or reused.

Every release candidate must pass a branch `workflow_dispatch` run of the Release pipeline before its immutable version tag is created. The release owner has sole write ownership of version sources, `deploy/**`, `tests/bootstrap/**`, release workflows, the root README release status, and translated README release statuses. Other contributors request changes to those paths in their report; they do not edit or bump them directly. One pull request covers one release topic and may merge only after required CI is green.
