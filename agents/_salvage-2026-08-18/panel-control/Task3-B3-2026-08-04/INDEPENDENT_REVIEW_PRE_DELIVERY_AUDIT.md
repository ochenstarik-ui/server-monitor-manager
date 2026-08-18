# INDEPENDENT REVIEW — Block B-3 + additive B-3R

## VERDICT: **APPROVE**

## Summary

The post-repair implementation closes the prior B1/H1 finalization race and H2 evidence gap. I found no blocking, high, medium, or low defects in the reviewed uncommitted diff.

The full B-3 and B-3R specifications and the pre-repair verdict were read before inspecting the diff. The review covered all 24 modified paths plus the untracked `ControlJsonContext.cs` on branch `hermes/task3-b3-fact-reconciliation`, base/HEAD `b11c277ac7f79a18670932eca4622982d9ff48e0`.

## Findings

### BLOCKING

None.

### HIGH

None.

### MEDIUM

None.

### LOW

None.

## B1/H1 concurrency closure

The repaired finalization architecture is adequate:

- Every staged endpoint is covered by one globally sorted node-lock acquisition:
  - `src/ServerMonitorManager.Control/LinkService.cs:569-573`
  - sorting and deduplication: `LinkService.cs:770-787`
- The current effective policy is read by natural key before selecting Link gates:
  - `LinkService.cs:574-579`
- Gates are selected from the current effective Link identity, falling back to the staged identity only when no current policy exists:
  - `LinkService.cs:580-583`
- Selected Link gates are deduplicated and globally sorted:
  - `LinkService.cs:808-825`
- The single shared final factual snapshot is taken only after all endpoint node locks and selected Link gates are held:
  - `LinkService.cs:584`
- The current effective policy is read again by natural key inside that boundary and after the factual snapshot:
  - `LinkService.cs:585-590`
- Identity, version, and desired state are revalidated before any final write, audit, or success/failure publication:
  - `LinkService.cs:597-602`
  - `LinkService.cs:631-637`
- Stale work exits through a synthetic result and does not call either finalization path:
  - `LinkService.cs:598-601`
  - synthetic classification only: `LinkService.cs:639-646`
- Actual-state writes and final events occur only on the non-stale branch:
  - success: `LinkService.cs:603-615`, `LinkService.cs:648-675`
  - failure: `LinkService.cs:617-625`, `LinkService.cs:679-694`
- Orphan audit persistence precedes the final `link.orphan-removed` event:
  - audit: `LinkService.cs:670-673`
  - event: `LinkService.cs:674`
- A policy appearing during DB-less orphan cleanup is safe:
  - node locks prevent production `CreateAsync`, which obtains the same endpoint locks at `LinkService.cs:23-25`;
  - pre-gate and in-gate natural-key reads detect an already-present policy;
  - any out-of-band store mutation after the final snapshot is rejected by the second identity/version/desired-state revalidation.

No new lock class was introduced. Production mutation paths retain the required node-locks-before-Link-gate ordering where endpoint serialization is required.

### Deterministic race-test decision

`BatchFinalizationDoesNotFinalizeOrPublishStalePolicyVersion` is sufficient together with the synchronization architecture; more blocking tests are not required.

The test deliberately pauses the second, final `ListRulesAsync` after its snapshot has been captured:

- pause configured for the second list:  
  `tests/ServerMonitorManager.Control.Tests/LinkReconciliationTests.cs:123-127`
- policy version/desired state changed while finalization is suspended:  
  `LinkReconciliationTests.cs:128-135`
- stale pass classified without overwriting the newer state:  
  `LinkReconciliationTests.cs:137-145`
- stale success events explicitly rejected:  
  `LinkReconciliationTests.cs:146-152`
- fake snapshot is captured before the pause, so the test exercises the strongest prior window—factual snapshot already stale before DB/event finalization:  
  `LinkReconciliationTests.cs:825-831`

The remaining previously requested Create/Disable/Reconnect interleavings are excluded architecturally by the full endpoint-node-lock plus selected-Link-gate boundary. Duplicating each impossible production interleaving as another blocking test would not materially increase acceptance confidence. The stale branch is also visibly side-effect-free in the implementation.

## H2 and trimmed-runtime evidence

`TRIMMED_NATIVE_POST_RACE_REPAIR.log` establishes the required Linux and published-runtime evidence:

- Linux Control suite: 94 passed, 0 failed, 0 skipped:
  - `TRIMMED_NATIVE_POST_RACE_REPAIR.log:116-119`
- `linux-x64` trimmed publication completed:
  - publish analysis/build: `TRIMMED_NATIVE_POST_RACE_REPAIR.log:120-146`
  - published executable checksum: `TRIMMED_NATIVE_POST_RACE_REPAIR.log:147`
- Published runtime orphan audit contains the expected named DTO JSON:
  - `TRIMMED_NATIVE_POST_RACE_REPAIR.log:148-150`
- Exact helper cardinality is two lists and one disconnect:
  - summary: `TRIMMED_NATIVE_POST_RACE_REPAIR.log:151`
  - invocation sequence: `TRIMMED_NATIVE_POST_RACE_REPAIR.log:153-156`

The IL2026 warnings at log lines 124-145 are confined to pre-existing provisioning serialization in `ProvisioningBaseInstallPlanStore.cs`, `ProvisioningFactsStore.cs`, and `ProvisioningStore.cs`. None of those files is in the B-3/B-3R diff. The B-3 orphan audit itself uses a named source-generated DTO:

- DTO/context: `src/ServerMonitorManager.Control/ControlJsonContext.cs:5-13`
- serialization: `src/ServerMonitorManager.Control/ControlStore.cs:1376-1398`

The warnings remain legitimate project technical debt, but they do not invalidate the explicitly executed trimmed orphan path or block this scope.

## Other requirements verified

- Exact batching:
  - initial list: `LinkService.cs:164-181`
  - one final shared list only after attempted mutations: `LinkService.cs:342-348`, `LinkService.cs:584`
  - no-drift one-call test: `LinkReconciliationTests.cs:58-79`
  - multi-mutation two-list test: `LinkReconciliationTests.cs:81-106`
- Exhaustive result invariant:
  - full pass: `LinkService.cs:894-928`
  - node pass: `ControlStore.cs:1774-1806`
  - mixed six-policy test: `LinkReconciliationTests.cs:279-311`
- Deferred semantics and marker consumption:
  - classification: `LinkService.cs:235-249`, `LinkService.cs:315-323`
  - only failures block marker completion: `LinkReconciliationBackgroundService.cs:69-85`
  - ten-pass Deferred scheduling test: `LinkReconciliationTests.cs:313-351`
- Three-attempt marker throttle and single warning:
  - `LinkReconciliationBackgroundService.cs:40-45`
  - `LinkReconciliationBackgroundService.cs:74-93`
  - `LinkReconciliationBackgroundService.cs:102-112`
- Firewall fail-stop and aggregate event:
  - `LinkService.cs:164-174`
  - final-list failure: `LinkService.cs:342-352`
  - aggregate completion: `LinkService.cs:709-759`
- Mandatory factual interfaces have no fail-open defaults:
  - `LinkPolicyApplier.cs:7-13`
  - all repository implementations explicitly implement both members.
- Reserved-node and malformed-registry handling:
  - `deploy/ochenstarik-smm-policy-apply:50-63`
  - production-shaped contract fixtures in `tests/bootstrap/test-bootstrap-contract.sh`
- Managed/foreign/malformed comment boundary:
  - `deploy/ochenstarik-smm-policy-apply:158-189`
- Helper arity and exit classification:
  - `deploy/ochenstarik-smm-policy-apply:83-115`
  - `deploy/ochenstarik-smm-policy-apply:239-262`
- Generation-safe marker handling:
  - `deploy/ochenstarik-smm-policy-apply:205-237`
- Retention only for old latest `Disabled/Disabled` natural keys:
  - `ControlStore.cs:355-388`
  - regression test: `ControlMaintenanceTests.cs:112-166`
- Desktop default effective Active/drift filter, explicit history toggle, and unambiguous displayed-set counters:
  - `LinksPage.xaml.cs:20-41`
  - `LinksPage.xaml:78-106`
- Pending activation is presented as a non-error:
  - `MeshModels.cs:65-74`
  - `MainPage.xaml.cs:1265-1275`
- `IsEligibleForFullReconciliation` is absent.
- No Block C implementation appears in the diff.

## Executed review evidence

Independently executed:

- `bash tests/bootstrap/test-bootstrap-contract.sh` — `BOOTSTRAP_CONTRACT=PASS`
- `bash -n deploy/ochenstarik-smm-policy-apply` — PASS
- `bash -n tests/acceptance/three-server-mesh.sh` — PASS
- `powershell.exe ... tests/windows/Test-DesktopContracts.ps1` — `Windows desktop contracts passed.`
- tracked `git diff --check b11c277...` — PASS
- untracked `ControlJsonContext.cs` whitespace check — no errors
- final branch/HEAD/status rechecked; inspected state remained the same.

Local `dotnet test` could not be independently rerun because `dotnet` is unavailable in this reviewer shell. The parent’s Windows 94/94, Desktop Release build, and other build claims were therefore treated as supplied evidence rather than independently reproduced. The separate native Linux log provides direct 94/94 and trimmed-runtime evidence.

## Review side effects and residual scope

- Files created or modified by reviewer: **none**
- Physical three-server acceptance remains the documented external blocker and is not represented as completed.
- The working tree remains intentionally uncommitted; no commit, push, PR, or merge action was performed.