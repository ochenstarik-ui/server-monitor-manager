# Final B-3R PublishTrimmed verification

## Snapshot

- Reviewed commit: `ee7d89c1d02cba7e0418637282ce274b60edde69`
- Merge commit: `00dadf27cebbb8f311337f21ebdeadd90c1a9f8c`
- WSL2 Ubuntu 26.04
- .NET SDK `10.0.302`
- `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`

## Results

- Clean Linux Control: `94/94 PASS`.
- Self-contained `linux-x64 PublishTrimmed`: completed.
- Published Control SHA256: `0a8ff5af6bc62b7c4b0cb9c33ddf032fcbc15e8bafc0bee33c9fb281250520fa`.
- Published executable started with trim-safe generated configuration binding.
- DB-less orphan rule was removed.
- Durable orphan audit exact payload was found and parsed.
- Audit ordering remained factual verification → durable audit → `link.orphan-removed` event.
- Helper cardinality: exactly `2` `link-list` calls and `1` `link-disconnect` call.
- Final marker: `TRIMMED_NATIVE_ORPHAN_AUDIT=PASS`.

Authoritative log: `TRIMMED_NATIVE_POST_RACE_REPAIR.log`.

## Warnings

The publish log includes known `IL2026` warnings from pre-existing provisioning serializers outside the B-3/B-3R changed paths. The B-3 orphan audit uses `ControlJsonContext` and its named DTO, and its published runtime path passed.
