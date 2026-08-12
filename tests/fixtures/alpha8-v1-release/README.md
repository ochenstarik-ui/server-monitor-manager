# Synthetic pre-alpha.9 release fixture

This deterministic fixture models the published alpha.8 v1 schema and release layout: `server-monitor-manager-bootstrap-manifest.json`, a locally generated archive, and its sibling `.sha256`, with no manifest v2 and no usable signature. The test generates the archive payload locally and never downloads a published release. The v1 manifest's `bootstrap_sha256` is pinned to the synthetic bootstrap payload and checked before the archive scenarios run.
