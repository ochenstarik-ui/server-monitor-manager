# B-3R Linux PublishTrimmed and orphan-audit verification

## Environment

- WSL2 Ubuntu 26.04
- .NET SDK `10.0.302`
- .NET runtime `10.0.10`
- `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`
- Clean source-only snapshot: `/home/starik/hermes-b3r-native.z4QMkUHO/source`
- Published artifact: `/home/starik/hermes-b3r-native.z4QMkUHO/publish`

## Results

| Gate | Result |
|---|---|
| Clean Linux Control Release suite | **93/93 PASS** |
| `linux-x64 --self-contained true -p:PublishTrimmed=true` | **PASS** |
| Native published executable startup | **PASS** |
| DB-less factual orphan removal | **PASS** |
| Factual call count | **2 `link-list`, 1 `link-disconnect`** |
| Durable orphan audit | **PASS** |

Published `ochenstarik-smm-control.dll` SHA256:

```text
5d3c1df08ec91e129689745ec7fd18f2ed9ff8cb2e2e5e03b3b32b4107bc53b1
```

Exact audit row observed from the SQLite database created by the native trimmed executable:

```text
system:reconcile|link.orphan-removed|source:target:tcp:2222|{"sourceNodeId":"source","targetNodeId":"target","protocol":"tcp","port":2222}
```

Exact helper sequence:

```text
reconcile-status
link-list
link-disconnect source target tcp 2222
link-list
```

## RED→GREEN defect found during verification

Initial trimmed artifacts ignored configuration overrides because `ControlOptions` used init-only properties. The native process attempted the default `/var/lib/ochenstarik-server-monitor-manager` path and failed with `UnauthorizedAccessException`.

Repair:

- enabled `.NET` configuration binding source generation;
- changed `ControlOptions` from init-only properties to standard settable options properties;
- retained trim metadata preservation on the options type.

Post-repair evidence:

- focused API regressions: **2/2 PASS**;
- full Windows Control suite: **93/93 PASS**;
- clean Linux suite: **93/93 PASS**;
- native trimmed orphan path: **PASS**.

## Known warnings outside B-3

The publish log contains `IL2026` warnings in provisioning serialization paths (`ProvisioningStore`, `ProvisioningBaseInstallPlanStore`, `ProvisioningFactsStore`). These warnings predate and are outside the B-3 Link/orphan scope. The B-3 orphan audit itself uses the named DTO and source-generated JSON context and was exercised successfully above.

Primary raw log: `TRIMMED_NATIVE_ORPHAN_AUDIT.log`.
Historical RED logs are retained for reproducibility.
