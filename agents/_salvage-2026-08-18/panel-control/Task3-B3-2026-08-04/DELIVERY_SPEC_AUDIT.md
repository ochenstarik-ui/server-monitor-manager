# B-3R delivery-spec audit

## Scope

Audit target: final merged `main` commit `d645812d29d077e9a4dee1596ef09a70dc138090`, base `b11c277ac7f79a18670932eca4622982d9ff48e0`, implementation PR #16 and test-only delivery-review repair PR #17.

Documents read directly:

1. `smm-task-B3-spec-2026-08-03.md`;
2. `smm-task-B3R-spec-2026-08-04.md`;
3. `smm-hermes-task-b3r-2026-08-06.md`;
4. `smm-hermes-task-b3r-deliver-2026-08-07.md`.

## Privileged-call budget

`ReconcileAllAsync` creates one `FullReconciliationBatch` and passes it to every full-pass `ConvergeAsync` call (`LinkService.cs:183,218-224,297-304`). The four direct `VerifyExactFactualCountAsync` call sites are all guarded by `if (batch is null)` (`LinkService.cs:484-514`). They therefore apply to direct Create/Disable/TTL/retry paths, not to the full reconciliation pass.

For a full pass:

- initial factual list: `LinkService.cs:167`;
- shared final factual list, only when a mutation was attempted: `LinkService.cs:342-348,584`;
- no-drift assertion: `SecondUnchangedPassDoesNotMutateFirewall` requires one privileged call;
- k-mutation assertion: `FullPassWithMultipleMutationsUsesOneInitialAndOneFinalList` requires four mutations and exactly two list calls.

Result: no drift = exactly one `link-list`; one or more mutation attempts = exactly two `link-list` calls.

## Seven mandatory scenarios

| # | Contract | Executable coverage | Exact assertion/evidence | Parent audit |
|---|---|---|---|---|
| 1 | DB-less orphan raw disconnect reports success but rule remains → Failed | `DatabaseLessOrphanIsFailedWhenDisconnectReportsSuccessButRuleRemains` | `LinkReconciliationTests.cs:196-210`: `(Examined,Converged,Failed)=(1,0,1)`, rule remains, orphan ID in failed list | PASS |
| 2 | Persisted Disabled with missing Node row verifies removal through list and remains Disabled | `PersistedDisabledCleanupDoesNotProbeMissingNodeStatus` | `LinkReconciliationTests.cs:214-233`: status probe configured to throw, pass still converges and persisted actual state is `Disabled` | PASS |
| 3 | Failed retains marker; Converged+Deferred consumes it | `MarkerIsRetainedWhenPostMutationFactCannotBeVerified`; `DeferredPoliciesConsumeMarkerAndDoNotCreatePromptHotLoop` | `LinkReconciliationTests.cs:253-277`: zero completion calls and marker remains; `:314-347`: first deferred pass completes marker and clears request | PASS |
| 4 | Six-policy mixed invariant across all three buckets | `MixedPassClassifiesEveryExaminedPolicyExactlyOnce` plus constructor guard `ReconciliationResultsRejectNonExhaustiveClassificationsWithIds` | `LinkReconciliationTests.cs:280-310`: six examined, `(2,2,2)`, exact sum and disjoint IDs; `:17-29`: non-exhaustive result throws with IDs | PASS |
| 5 | k mutations use exactly two list calls | `FullPassWithMultipleMutationsUsesOneInitialAndOneFinalList` | `LinkReconciliationTests.cs:82-106`: four repairs, four connects, exactly two list calls | PASS |
| 6 | Production-shape four-field Node rows and status/IP outcomes | `tests/bootstrap/test-bootstrap-contract.sh` real-helper contract | `:88-93` active+valid success; `:142-152` reserved+valid → 80; `:153-161` absent target row → 80; `:162-191` active blank/invalid/out-of-range IP → 78; `:192-201` malformed status → 78 | PASS |
| 7 | Reserved Node over ten scheduled passes remains Deferred, consumes marker, no hot loop | `DeferredPoliciesConsumeMarkerAndDoNotCreatePromptHotLoop` | `LinkReconciliationTests.cs:333-350`: ten iterations, exact Deferred tuple every time, first marker completion, immediate extra run is null, 30-second advances, no prompt-exhausted warning | PASS |

Focused execution on merged main:

- six unique xUnit methods representing requirements 1-5 and 7: **6 passed, 0 failed, 0 skipped**;
- production-shape helper/bootstrap contract: `BOOTSTRAP_CONTRACT=PASS`;
- Linux-only fact-first integration under native WSL `/tmp` copy: **1 passed, 0 failed, 0 skipped**.

Evidence:

- `MANDATORY_SEVEN_TESTS.log`;
- `MANDATORY_LINUX_INTEGRATION.log`;
- full clean Linux suite and published runtime: `TRIMMED_NATIVE_POST_RACE_REPAIR.log`.

## Delivery state

- PR #16: merged;
- PR #17 test-only missing-row regression repair: merged;
- PR checks: 14/14 pass;
- final-main post-merge workflows: 3/3, 12/12 jobs pass;
- clean WSL Linux Control: 94/94 pass;
- published `linux-x64 PublishTrimmed` orphan path: pass with exact audit JSON and `list:2, disconnect:1`;
- reports separate local, CI, native, and not-run evidence;
- physical acceptance: not run because all seven topology/SSH inputs are unset.

## Review formality

The authoritative provider-separated reviewer read all four complete documents and the complete implementation diff, then returned **APPROVE**. Its two non-blocking LOW findings were closed: PR #17 added the explicit absent-target-row helper regression, and the package integrity manifest was regenerated after all supplemental evidence was added. See `INDEPENDENT_REVIEW.md` and `DELIVERY_REVIEW_FINDINGS_CLOSURE.md`.
