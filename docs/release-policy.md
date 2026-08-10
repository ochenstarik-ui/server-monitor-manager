# Release policy

Published tags and release assets are immutable.

A tag that has been published must never be moved, reused, deleted and recreated, or supplied with replacement assets under the same names. If a published build or installer is wrong, preserve the existing release and publish a new, higher version tag containing the correction.

Release artifacts are built from the commit named by the tag through the repository release workflows, including `.github/workflows/linux-release.yml` and `.github/workflows/windows-release.yml`. The tracked production source for the convenience installer is `deploy/smm-setup.sh`; the Linux release workflow copies that exact file to the release artifact set, records its SHA-256 in the signed manifest, and publishes its standalone checksum. The default release in that source must match the tag being produced.

For `v0.1.0-alpha.9`, this makes `smm-setup.sh`, `smm-setup.sh.sha256`, the bootstrap script, platform archives, SBOMs, and the signed manifest reproducible from the tagged tree. The installer fetches only same-tag assets and verifies the bootstrap checksum before execution. Corrections after publication require another tag; the `v0.1.0-alpha.9` tag and assets remain unchanged.
