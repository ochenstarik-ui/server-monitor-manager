# B-3/B-3R batch-finalization race repair evidence

## Finding

Independent pre-repair review returned `REQUEST_CHANGES` because the mutation phase released endpoint/current-Link locks before the shared final factual list and `FinalizeBatchAsync`. A concurrent desired-version change could therefore cause stale actual-state writes and stale success/failure/audit telemetry.

Source: `INDEPENDENT_REVIEW_PRE_REPAIR.md`.

## RED → GREEN

A deterministic regression test was added before the production repair:

- `LinkReconciliationTests.BatchFinalizationDoesNotFinalizeOrPublishStalePolicyVersion`
- it pauses the shared final list after mutation staging;
- changes the effective policy version/desire during that exact window;
- asserts that the newer desired/actual state is not overwritten and stale `link.active`/`link.reapplied` events are not published.

The worker transcript records:

- pre-production-patch focused test: exit `1` (RED; assertion payload was compacted in the transcript and is not reconstructed here);
- post-patch focused test: `1/1 PASS`;
- all reconciliation tests: `24/24 PASS`;
- worker full Windows Control: `94/94 PASS`.

Full transcript: `RACE_REPAIR_WORKER_TRANSCRIPT.log`.

## Repair

`src/ServerMonitorManager.Control/LinkService.cs` now performs batch finalization under the existing required synchronization hierarchy:

1. all distinct endpoint node locks in ordinal order;
2. effective-policy natural-key read;
3. all selected current per-Link gates in ordinal order;
4. the one shared final `ListRulesAsync`;
5. a second effective-policy natural-key read inside the gates;
6. identity/version/desired-state validation before any DB write, event, or audit.

Stale staged work is classified once as failed without mutating the current policy or publishing stale telemetry. A DB-less orphan becomes stale if a persisted effective policy appears before finalization, preventing a false orphan audit/event.

B-3R batching remains unchanged:

- no mutation attempts: one initial list;
- one or more mutation attempts: one initial list plus one shared final list;
- no per-item factual list during full-pass finalization.

## Parent post-repair verification

### Windows and contracts

- Control Release: `94/94 PASS`.
- Bootstrap contract: `BOOTSTRAP_CONTRACT=PASS`.
- Helper syntax: PASS.
- Three-server acceptance script syntax: PASS.
- Desktop contracts: PASS.
- Desktop x64 Release: `0 warnings`, `0 errors`.
- `git diff --check`: PASS.

Logs:

- `WINDOWS_CONTROL_POST_RACE_REPAIR.log`
- `DESKTOP_CONTRACTS_POST_RACE_REPAIR.log`
- `DESKTOP_BUILD_POST_RACE_REPAIR.log`

### Clean Linux + native trimmed artifact

Correct WSL invocation of the native harness produced:

- clean Linux Control: `94/94 PASS`;
- self-contained `linux-x64 PublishTrimmed`: completed;
- published Control SHA256: `0a8ff5af6bc62b7c4b0cb9c33ddf032fcbc15e8bafc0bee33c9fb281250520fa`;
- native orphan audit exact payload: PASS;
- `TRIMMED_NATIVE_ORPHAN_AUDIT=PASS`;
- `HELPER_CALLS=list:2,disconnect:1`.

Log: `TRIMMED_NATIVE_POST_RACE_REPAIR.log`.

The publish emits known `IL2026` warnings from pre-existing provisioning serialization paths. The B-3 orphan audit itself uses a named DTO and source-generated JSON context and was executed successfully in the published trimmed executable.

Two earlier reruns invoked the Linux harness under Git Bash instead of WSL and failed before build because `/mnt/c` does not exist in that shell. They are retained as infrastructure RED evidence and are not product-test results:

- `TRIMMED_NATIVE_POST_RACE_REPAIR_MOUNT_RED.log`
- `TRIMMED_NATIVE_POST_RACE_REPAIR_WRONG_SHELL_RED.log`

## Delivery status

This is pre-commit evidence. Commit/push/PR/merge remain blocked until the provider-separated post-repair reviewer returns explicit `APPROVE`.
