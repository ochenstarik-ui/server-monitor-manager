# INDEPENDENT REVIEW — merged Block B-3 + B-3R

## Verdict: **APPROVE**

Reviewed immutable `main` commit `00dadf27cebbb8f311337f21ebdeadd90c1a9f8c` against base `b11c277ac7f79a18670932eca4622982d9ff48e0`, covering the complete 25-file `base..merge` diff: 1,895 insertions and 161 deletions.

The following four normative documents were read completely before assessing the implementation:

1. `smm-task-B3-spec-2026-08-03.md`
2. `smm-task-B3R-spec-2026-08-04.md`
3. `smm-hermes-task-b3r-2026-08-06.md`
4. `smm-hermes-task-b3r-deliver-2026-08-07.md`

The previous `INDEPENDENT_REVIEW.md` was treated only as historical context.

## Findings

### BLOCKING

None.

### HIGH

None.

### MEDIUM

None.

### LOW

1. **The authoritative B-3R request to preserve an explicit missing-row helper regression test is only indirectly covered.**

   - The helper directly implements missing-row → exit 80 at `deploy/ochenstarik-smm-policy-apply:53-54`.
   - The real-helper contract proves the same exit-80 function and exact `mesh.node-not-activated` marker through a production-shaped `reserved` row at `tests/bootstrap/test-bootstrap-contract.sh:142-152`.
   - Active valid, blank/invalid/out-of-range IP, and malformed-status cases are covered at `tests/bootstrap/test-bootstrap-contract.sh:88-94,153-192`.
   - There is no separate invocation where `target` is entirely absent from `nodes.tsv`, although authoritative B-3R requested that this pre-existing case be retained.

   **Acceptance effect:** non-blocking. The separate 2026-08-06 seven-test acceptance list does not require the absent-row case, the branch is a direct `[[ -n "$record" ]] || node_not_activated`, and the exact exit-80 marker path is exercised by the reserved-row case. Adding an explicit absent-row contract remains advisable to prevent future changes from conflating “missing” with corrupt registry state.

2. **Two supplementary evidence logs are not integrity-covered by `SHA256SUMS`.**

   - `SHA256SUMS:1-49` validates all 49 entries it lists, but the manifest ends without entries for:
     - `MANDATORY_LINUX_INTEGRATION.log`
     - `MANDATORY_SEVEN_TESTS.log`
   - Those files contain evidence at `MANDATORY_LINUX_INTEGRATION.log:1-11` and `MANDATORY_SEVEN_TESTS.log:1-14`.

   **Acceptance effect:** non-blocking for the immutable merged implementation. All 49 listed checksums independently matched, and the omitted results are corroborated by the full Linux/Windows/CI evidence. The delivery package should regenerate its manifest after replacing this review so every final package file is covered.

## Seven-test acceptance table

| # | Exact test/script | Assertions inspected | Assessment |
|---:|---|---|---|
| 1 | `LinkReconciliationTests.DatabaseLessOrphanIsFailedWhenDisconnectReportsSuccessButRuleRemains` | Configures `DisconnectLeavesRules=true`; after `ReconcileAllAsync`, asserts `(Examined, Converged, Failed) == (1,0,1)`, the rule remains in the final factual listing, and `FailedPolicyIds` contains the synthetic `orphan:` ID (`tests/ServerMonitorManager.Control.Tests/LinkReconciliationTests.cs:195-211`). | **PASS — direct coverage.** Proves helper success is insufficient without exact post-mutation cardinality. |
| 2 | `LinkReconciliationTests.PersistedDisabledCleanupDoesNotProbeMissingNodeStatus` | Starts from a persisted disabled policy, reinjects its rule, configures status probing to throw node-not-activated, then asserts `(1,1,0)` and persisted `ActualState == "Disabled"` (`LinkReconciliationTests.cs:213-233`). | **PASS — direct behavioral coverage.** The test would fail if cleanup depended on `link-status`; production uses the final `link-list`. |
| 3 | `LinkReconciliationTests.MarkerIsRetainedWhenPostMutationFactCannotBeVerified` **plus** `LinkReconciliationTests.DeferredPoliciesConsumeMarkerAndDoNotCreatePromptHotLoop` | Failed final factual verification asserts `(1,0,1)`, zero completion calls, and retained generation (`LinkReconciliationTests.cs:252-277`). Deferred-only completion asserts `(1,0,0,1)`, exact deferred ID, marker consumed once, and immediate repeat throttled (`LinkReconciliationTests.cs:313-347`). | **PASS.** Failed and Deferred sides are directly tested. A combined Converged+Deferred set is indirect, but non-blocking: production completion checks only `result.Failed > 0` at `LinkReconciliationBackgroundService.cs:69-85`; converged-only completion is separately covered by `MarkerCreatedAfterNormalPassTriggersPromptAdditionalPass` at `LinkReconciliationTests.cs:541-565`. |
| 4 | `LinkReconciliationTests.MixedPassClassifiesEveryExaminedPolicyExactlyOnce` | Builds six policies with two converged, two mutation failures, and two deferred connects; asserts `Examined == 6`, `(Converged, Failed, Deferred) == (2,2,2)`, sum equals examined, two IDs in each non-success bucket, and no overlap (`LinkReconciliationTests.cs:279-311`). | **PASS — direct six-policy invariant coverage.** Constructor-level rejection of non-exhaustive results is additionally tested at `LinkReconciliationTests.cs:17-29`. |
| 5 | `LinkReconciliationTests.FullPassWithMultipleMutationsUsesOneInitialAndOneFinalList` | Creates four policies, removes all factual rules, runs the full pass, then asserts `(4,4,0,0)`, exactly two additional `ListRulesAsync` calls, and exactly four connects (`LinkReconciliationTests.cs:81-106`). | **PASS — direct `k=4` batching-budget proof.** |
| 6 | `tests/bootstrap/test-bootstrap-contract.sh` | Active four-field source/target rows produce a valid connect command (`:88-94`); reserved+valid IP gives exit 80 and exact marker (`:142-152`); active+blank, invalid, and out-of-range IP each give exit 78 and exact diagnostic (`:153-182`); malformed status gives exit 78 and exact diagnostic (`:183-192`). Script completed with `BOOTSTRAP_CONTRACT=PASS`. | **PASS.** Production-shaped real-helper coverage. Completely absent target-row behavior is indirect as noted under LOW, but does not invalidate this mandatory seven-test item. |
| 7 | `LinkReconciliationTests.DeferredPoliciesConsumeMarkerAndDoNotCreatePromptHotLoop` | Executes ten scheduled iterations; every pass is `(1,0,0,1)` with the exact policy ID, first pass consumes the marker, immediate extra invocation returns null, subsequent passes occur only after 30-second advances, and no “prompt exhausted” warning appears (`LinkReconciliationTests.cs:313-351`). | **PASS.** Repeated Deferred scheduling is directly tested; real reserved-row → exit 80 is separately proven by the helper contract, so the end-to-end composition is indirect but adequate and non-blocking. |

`MANDATORY_SEVEN_TESTS.log:2-14` records six focused xUnit tests passing with zero skipped plus the production-shaped helper contract passing. The apparent count of six xUnit methods is correct because `DeferredPoliciesConsumeMarkerAndDoNotCreatePromptHotLoop` satisfies both the Deferred half of criterion 3 and criterion 7.

## Full-pass list budget versus direct paths

The required full-pass budget is satisfied:

- Initial factual snapshot: `LinkService.ReconcileAllAsync` calls `ListRulesAsync` once at `src/ServerMonitorManager.Control/LinkService.cs:164-181`.
- Both full-pass convergence calls receive the same non-null `FullReconciliationBatch`:
  - persisted candidates: `LinkService.cs:218-224`
  - factual orphans: `LinkService.cs:297-304`
- All four per-mutation `VerifyExactFactualCountAsync` calls are guarded by `if (batch is null)`:
  - duplicate cleanup: `LinkService.cs:480-487`
  - connect: `LinkService.cs:489-496`
  - persisted disconnect: `LinkService.cs:498-505`
  - DB-less raw disconnect: `LinkService.cs:507-514`
- Therefore they belong only to direct Create, Disable/certificate cleanup, reconnect, TTL, and retry paths, whose callers omit a batch at `LinkService.cs:44,89,100,131-135,422-423`.
- A full pass with any attempted mutation takes exactly one shared final snapshot in `FinalizeBatchAsync` at `LinkService.cs:342-348,565-584`.
- No-drift budget is asserted as one total privileged call and zero changed mutation counts by `SecondUnchangedPassDoesNotMutateFirewall` at `LinkReconciliationTests.cs:58-79`.
- Multi-mutation budget is asserted as two lists for four mutations at `LinkReconciliationTests.cs:81-106`.
- The native trimmed orphan run independently records `link-list`, raw disconnect, `link-list` and `HELPER_CALLS=list:2,disconnect:1` at `TRIMMED_NATIVE_POST_RACE_REPAIR.log:148-156`.

Result: **no hidden `1+k` full-pass path remains**.

## Race, locking, batching, and result accounting

The prior stale-finalization race is adequately repaired:

- Per-item mutation work retains sorted endpoint node locks before the selected Link gate at `LinkService.cs:192-205,268-279`.
- Batch finalization acquires all distinct endpoint locks in ordinal order at `LinkService.cs:569-573,770-787`.
- It reads the current effective policy by natural key before gate selection at `LinkService.cs:574-579`.
- Current Link gates are deduplicated and globally sorted at `LinkService.cs:580-583,808-825`.
- The single final factual snapshot occurs only after those locks and gates are held at `LinkService.cs:584`.
- The effective natural-key row is re-read after the snapshot at `LinkService.cs:585-590`.
- Identity, version, desired state, or newly appearing persisted orphan state makes staged work stale at `LinkService.cs:631-646`; stale work does not write state, audit, or success events.
- `BatchFinalizationDoesNotFinalizeOrPublishStalePolicyVersion` pauses after capture of the second list, changes desired version, then asserts one failure, no overwrite of the new state, and no stale `link.active`/`link.reapplied` event at `LinkReconciliationTests.cs:108-152,825-831`.
- A factual orphan adopted as an active policy while waiting for node locks is not removed, as asserted by `FactualOrphanAdoptedAsActiveUnderNodeLocksIsNotRemoved` at `LinkReconciliationTests.cs:512-538`.
- No new lock class was introduced; synchronization remains sorted node locks → per-Link gate.
- Full- and node-pass constructors enforce `Converged + Failed + Deferred == Examined` at `LinkService.cs:894-928` and `ControlStore.cs:1774-1806`.

## Security and privileged-helper boundary

No blocking boundary regression was found:

- `link-list` requires exactly one argument at `deploy/ochenstarik-smm-policy-apply:239-244`.
- All helper execution remains argument-list based with `UseShellExecute=false`, non-interactive sudo, and no shell interpolation at `LinkPolicyApplier.cs:126-141`.
- Exit 79 is recognized only with the exact `mesh.firewall-unavailable` marker; exit 80 only with the exact node-not-activated marker at `LinkPolicyApplier.cs:148-163`.
- `LC_ALL=C` and root/test-mode boundaries remain explicit at `ochenstarik-smm-policy-apply:1-27`.
- Missing `/usr/sbin/nft` is fail-closed and separate from missing-table classification at `ochenstarik-smm-policy-apply:83-115`.
- `link-list` exposes only validated `smm:<source>:<target>:<protocol>:<port>` comments; foreign rules are skipped, malformed managed-looking comments are diagnosed, and duplicates are retained for reconciliation at `ochenstarik-smm-policy-apply:158-189`.
- Raw disconnect deletes every handle with the exact managed comment and no longer consults `nodes.tsv` at `ochenstarik-smm-policy-apply:140-155`.
- Marker observation/completion is generation-aware under the privileged lock at `ochenstarik-smm-policy-apply:205-237`.
- The factual interfaces are compile-time mandatory: `ListRulesAsync` and raw `ApplyDisconnectAsync(LinkRule, …)` have no default implementation at `LinkPolicyApplier.cs:7-13`; all production/test implementations implement both.

## Audit, retention, Desktop, and scope boundaries

- DB-less orphan success is finalized only after exact zero cardinality; orphan audit is persisted before `link.orphan-removed` publication at `LinkService.cs:648-675`.
- Audit details use named `LinkOrphanAuditDetails` and source-generated `ControlJsonContext` at `ControlJsonContext.cs:5-13` and `ControlStore.cs:1376-1398`.
- Retention removes the complete history only when the latest natural-key row is old `Disabled/Disabled`; Active and drifted latest policies are preserved at `ControlStore.cs:355-388`. The regression test asserts two completed rows deleted, Active and Failed rows retained, and no older version resurrected at `ControlMaintenanceTests.cs:112-166`.
- `LinkRetentionDays` has a default, startup validation, appsettings value, and bootstrap environment value at `ControlOptions.cs:28-36`, `Program.cs:34-54`, `appsettings.json:6-16`, and `deploy/ochenstarik-server-monitor-manager.sh:493-503`.
- Desktop defaults to latest effective Active or drifted policies, offers an explicit history toggle, and calculates counters over the displayed set at `LinksPage.xaml.cs:20-41` and `LinksPage.xaml:78-106`.
- Pending activation is presented as an informational expected state rather than an error at `MeshModels.cs:65-74` and `MainPage.xaml.cs:1265-1276`.
- `IsEligibleForFullReconciliation` is absent.
- Changes outside Link reconciliation are limited to the required configuration, documentation, tests, retention, helper, and Desktop presentation work. No Block C implementation or unrelated Desktop refactor appears in the complete diff.

## Verification and evidence separation

### Independently executed during this review

- `git diff --check b11c277..00dadf2` — pass.
- `bash -n deploy/ochenstarik-smm-policy-apply` — pass.
- `bash -n tests/acceptance/three-server-mesh.sh` — pass.
- `bash tests/bootstrap/test-bootstrap-contract.sh` — `BOOTSTRAP_CONTRACT=PASS`.
- Live GitHub query confirmed PR #16 merged with:
  - base `b11c277ac7f79a18670932eca4622982d9ff48e0`
  - reviewed head `ee7d89c1d02cba7e0418637282ce274b60edde69`
  - merge `00dadf27cebbb8f311337f21ebdeadd90c1a9f8c`
- Live GitHub checks confirmed all 14 PR checks passed.
- Live commit-run query confirmed all three post-merge workflows completed successfully.
- Final repository status remained clean at the expected immutable merge SHA.
- All 49 checksums listed in `SHA256SUMS` matched.

### Supplied deterministic/native evidence inspected

- Windows committed Control: 94 passed, zero failed/skipped at `WINDOWS_CONTROL_COMMITTED.log:1-9`.
- Clean WSL Linux Control: 94 passed, zero failed/skipped at `TRIMMED_NATIVE_POST_RACE_REPAIR.log:116-119`.
- Published self-contained `linux-x64 PublishTrimmed` completed at `TRIMMED_NATIVE_POST_RACE_REPAIR.log:120-147`.
- The published executable executed the orphan path and emitted the exact durable audit JSON at `TRIMMED_NATIVE_POST_RACE_REPAIR.log:148-150`.
- Native helper cardinality was exactly two lists and one disconnect at `TRIMMED_NATIVE_POST_RACE_REPAIR.log:151-156`.
- Desktop Release build completed with zero warnings/errors at `DESKTOP_BUILD_POST_RACE_REPAIR.log:1-8`; Desktop contracts passed at `DESKTOP_CONTRACTS_POST_RACE_REPAIR.log:1`.
- PR evidence reports 14/14 checks passed with zero skipped at `CI_CHECKS.txt:1-20`.
- Post-merge evidence reports three successful workflows and 12/12 successful jobs with zero skipped at `POST_MERGE_CI.txt:1-21`, including Linux Control, Windows build/security/MSIX, native Ubuntu matrices, release archives, and Debian systemd restart matrices.
- Existing `IL2026` trim warnings are confined to unchanged provisioning serialization paths (`TRIMMED_NATIVE_POST_RACE_REPAIR.log:124-145`); they do not affect the executed B-3 orphan-audit path.

`REPORT.md:3-5,72-84` and `TEST_EVIDENCE.md:22-102` correctly separate Windows-local, Linux-native, PR CI, post-merge CI, and unexecuted physical acceptance. Contract/mock/native-artifact checks are not represented as physical topology acceptance.

## Residual physical acceptance

Physical three-server acceptance was **not run and is not claimed**. All required topology inputs remain `UNSET` at `PHYSICAL_INPUT_STATUS.txt:1-7`.

The outstanding command remains:

```bash
SMM_ACCEPT_RESTORE=1 SMM_ACCEPT_REBOOT=1 tests/acceptance/three-server-mesh.sh
```

The harness contains factual connectivity, firewall restore, injected disabled-policy orphan cleanup, backup restore, and reboot checks at `tests/acceptance/three-server-mesh.sh:183-311`, but script existence and syntax do not establish physical behavior.

Final delivery status remains:

**merged / verified, physical acceptance pending**

## Review side effects

- Repository or evidence files created or modified: **none**
- Commits, pushes, PR actions, merges, or evidence rewrites: **none**