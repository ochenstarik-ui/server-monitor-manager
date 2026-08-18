# Block B-3 + B-3R delivery report

## Final status

**merged / verified, physical acceptance pending**

- Repository: `https://github.com/ochenstarik-ui/server-monitor-manager`
- Implementation PR: https://github.com/ochenstarik-ui/server-monitor-manager/pull/16
- Delivery-review repair PR: https://github.com/ochenstarik-ui/server-monitor-manager/pull/17
- Reviewed implementation commit: `ee7d89c1d02cba7e0418637282ce274b60edde69`
- Test-only repair commit: `83cbb3b6fb4ec21631953ba070f7d5d999bcde07`
- Final squash merge commit on `main`: `d645812d29d077e9a4dee1596ef09a70dc138090`
- Base: `b11c277ac7f79a18670932eca4622982d9ff48e0`
- Complete-four-document independent verdict: **APPROVE**; two non-blocking LOW findings were subsequently closed
- Changed paths: **25**
- Diff: **1904 insertions, 161 deletions**

## Delivered scope

Block B-3 and additive mandatory B-3R were implemented together:

- factual `link-list` helper contract with strict argv/arity validation;
- fail-closed firewall classification and exact exit codes;
- one initial factual snapshot per full pass;
- exactly one shared final factual snapshot when mutation attempts occur;
- exact natural-key/multiplicity verification for all touched rules;
- Active repair, duplicate collapse, Disabled cleanup, and DB-less orphan removal;
- separate `Converged`, `Failed`, and `Deferred` buckets with exhaustive invariants;
- `PendingActivation` classified as Deferred;
- generation-marker completion, retention, bounded prompt retries, and firewall backoff;
- lock-safe batch finalization using sorted endpoint node locks, selected current Link gates, and repeated natural-key/version/desired-state validation;
- side-effect-free stale staged classification;
- named orphan-audit DTO with source-generated JSON context and audit-before-event ordering;
- completed Disabled/Disabled policy retention;
- Desktop effective/drift filtering, history toggle, counters, and non-error PendingActivation presentation;
- expanded Control, helper, bootstrap, Desktop, and acceptance contracts.

Block C was not included.

## Security boundaries

- Existing `deploy/ochenstarik-smm-policy-apply` remains the privileged policy target.
- Sudoers was not changed.
- No broad privilege target, shell interpolation, or `UseShellExecute` was added.
- Strict helper argv and arity checks remain fail-closed.
- Linux peer credential, enrollment, and root-owned helper boundaries were not weakened.
- Foreign nftables rules are ignored and not mutated.
- Malformed managed-looking comments are diagnosed but never accepted as factual rules.

## Review history

- Initial independent review: `REQUEST_CHANGES`.
- Pre-race-repair review: `REQUEST_CHANGES` for stale batch finalization and missing runtime evidence.
- Repair: strict RED→GREEN with deterministic final-list/finalization interleaving test.
- Final independent full B-3+B-3R review: **APPROVE**.
- Complete delivery review after reading all four normative documents: **APPROVE**, with two non-blocking LOW findings.
- LOW closure: explicit missing-row helper regression merged through PR #17; final package manifest regenerated after all evidence changes.

See:

- `INDEPENDENT_REVIEW_INITIAL.md`
- `INDEPENDENT_REVIEW_PRE_REPAIR.md`
- `INDEPENDENT_REVIEW.md`
- `INDEPENDENT_REVIEW_PRE_DELIVERY_AUDIT.md`
- `DELIVERY_REVIEW_FINDINGS_CLOSURE.md`
- `RACE_REPAIR_EVIDENCE.md`

## Artifacts

- `B3_B3R_COMPLETE.patch` — complete base-to-reviewed-commit patch; clean-applied to all 25 paths.
- `B3_SPEC.md` and `B3R_SPEC.md` — authoritative task contracts.
- `B3R_TASK_2026-08-06.md` and `B3R_DELIVERY_2026-08-07.md` — complete additive task and delivery contract.
- `DELIVERY_SPEC_AUDIT.md` — requirement-to-test map for all seven mandatory scenarios and the full-pass privileged-call budget.
- `MANDATORY_SEVEN_TESTS.log` — six unique focused xUnit methods plus the production-shape real-helper contract.
- `MANDATORY_LINUX_INTEGRATION.log` — Linux-only fact-first integration test executed under native WSL `/tmp`, not the Windows no-op path.
- `TEST_EVIDENCE.md` — deterministic, CI, and native runtime evidence.
- `CI_CHECKS.txt` — PR check set, 14/14 pass.
- `POST_MERGE_CI.txt` — main push workflows, 12/12 jobs pass.
- `DELIVERY_REPAIR_PR17_CHECKS.txt` and `DELIVERY_REPAIR_POST_MERGE_CI.txt` — PR #17 14/14 and final-main 12/12 repair evidence.
- `TRIMMED_NATIVE_POST_RACE_REPAIR.log` — clean Linux suite, PublishTrimmed, native audit and helper cardinality.
- `SHA256SUMS` — integrity manifest computed after all other package files.

## Known residuals

### Physical acceptance

The mandatory command was not executed:

`SMM_ACCEPT_RESTORE=1 SMM_ACCEPT_REBOOT=1 tests/acceptance/three-server-mesh.sh`

All required topology/SSH inputs are `UNSET`; see `PHYSICAL_INPUT_STATUS.txt`. Tests, native verification, independent approval, and CI do not replace this acceptance.

### Existing trim warnings

`PublishTrimmed` reports `IL2026` warnings in pre-existing provisioning serialization paths. They are outside the B-3/B-3R changed-file scope. The B-3 orphan audit uses its source-generated DTO/context and was executed successfully in the published native binary.
