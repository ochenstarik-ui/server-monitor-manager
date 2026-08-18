# Block B-3 + B-3R test evidence

## Final immutable snapshot

- Reviewed commit: `ee7d89c1d02cba7e0418637282ce274b60edde69`
- Implementation merge commit: `00dadf27cebbb8f311337f21ebdeadd90c1a9f8c`
- Test-only delivery repair: `83cbb3b6fb4ec21631953ba070f7d5d999bcde07`
- Final main commit: `d645812d29d077e9a4dee1596ef09a70dc138090`
- PRs: https://github.com/ochenstarik-ui/server-monitor-manager/pull/16 and https://github.com/ochenstarik-ui/server-monitor-manager/pull/17

## TDD race repair

Test added before production repair:

`LinkReconciliationTests.BatchFinalizationDoesNotFinalizeOrPublishStalePolicyVersion`

- focused pre-patch run: exit `1` — RED;
- focused post-patch run: **1/1 PASS**;
- all `LinkReconciliationTests`: **24/24 PASS**;
- full Windows Control after repair: **94/94 PASS**.

The transcript compacted the RED assertion payload; no Expected/Actual text is reconstructed. The exit sequence and production-patch ordering are preserved in `RACE_REPAIR_WORKER_TRANSCRIPT.log`.

## Parent deterministic verification

| Gate | Result | Evidence |
|---|---:|---|
| Windows Control Release, post-repair | 94/94 PASS | `WINDOWS_CONTROL_POST_RACE_REPAIR.log` |
| Windows Control Release, committed tree | 94/94 PASS | `WINDOWS_CONTROL_COMMITTED.log` |
| Bootstrap contracts | PASS | `WINDOWS_CONTROL_POST_RACE_REPAIR.log` |
| Helper bash syntax | PASS | same command group, exit 0 |
| Three-server acceptance script syntax | PASS | same command group, exit 0 |
| Windows Desktop contracts | PASS | `DESKTOP_CONTRACTS_POST_RACE_REPAIR.log` |
| Desktop x64 Release | 0 warnings / 0 errors | `DESKTOP_BUILD_POST_RACE_REPAIR.log` |
| `git diff --check` | PASS | post-repair and committed checks |
| Clean-base patch apply | 25/25 paths PASS | delivery apply-check output |

## Clean Linux and published native runtime

Executed under WSL2 Ubuntu 26.04 with .NET SDK `10.0.302` and `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`:

- clean Linux Control suite: **94 passed, 0 failed, 0 skipped**;
- self-contained `linux-x64 PublishTrimmed`: completed;
- published Control SHA256: `0a8ff5af6bc62b7c4b0cb9c33ddf032fcbc15e8bafc0bee33c9fb281250520fa`;
- published executable started successfully with generated configuration binding;
- exact durable audit payload:
  `system:reconcile|link.orphan-removed|source:target:tcp:2222|{"sourceNodeId":"source","targetNodeId":"target","protocol":"tcp","port":2222}`;
- `TRIMMED_NATIVE_ORPHAN_AUDIT=PASS`;
- helper sequence: `link-list`, `link-disconnect source target tcp 2222`, `link-list`;
- cardinality: `list:2, disconnect:1`.

Evidence: `TRIMMED_NATIVE_POST_RACE_REPAIR.log`.

The two `*_WRONG_SHELL_RED.log` / `*_MOUNT_RED.log` files are infrastructure RED evidence from invoking the Linux harness under Git Bash rather than WSL. They failed before product build and are not counted as product-test results.

## Independent review

Final full-spec/provider-separated review:

- verdict: **APPROVE**;
- BLOCKING: 0;
- HIGH: 0;
- MEDIUM: 0;
- LOW: 2, both non-blocking and subsequently closed.

LOW closure consists of the explicit absent-target-row helper regression merged through PR #17 and regeneration of the final integrity manifest. Evidence: `INDEPENDENT_REVIEW.md` and `DELIVERY_REVIEW_FINDINGS_CLOSURE.md`.

## Delivery-note mandatory scenarios

The seven acceptance scenarios from the complete `smm-hermes-task-b3r-2026-08-06.md` were mapped to exact test methods and helper assertions in `DELIVERY_SPEC_AUDIT.md`.

Focused execution on merged `main`:

- six unique xUnit methods covering requirements 1-5 and 7: **6 passed, 0 failed, 0 skipped**;
- production-shape real-helper/bootstrap contract for requirement 6: **PASS**;
- Linux-only fact-first integration test under a native WSL `/tmp` source copy: **1 passed, 0 failed, 0 skipped**.

The first direct `/mnt/c` filtered attempt only built and produced no test summary; it was not counted. The accepted Linux evidence is the native-filesystem run in `MANDATORY_LINUX_INTEGRATION.log` with an explicit `Passed: 1, Skipped: 0` summary.

Evidence:

- `MANDATORY_SEVEN_TESTS.log`;
- `MANDATORY_LINUX_INTEGRATION.log`;
- `DELIVERY_SPEC_AUDIT.md`.

## Pull request CI

PR #16 final check set:

- total checks: **14**;
- pass: **14**;
- skipped: **0**;
- non-green: **0**.

Evidence: `CI_CHECKS.txt` and `CI_CHECKS_WATCH.log`.

PR #17 test-only delivery repair:

- total checks: **14**;
- pass: **14**;
- skipped/non-green: **0**.

Evidence: `DELIVERY_REPAIR_PR17_CHECKS.txt` and `DELIVERY_REPAIR_PR17_CI.log`.

## Post-merge main CI

Merge SHA push workflows:

- workflows: **3/3 success**;
- jobs: **12/12 success**;
- skipped: **0**;
- non-green: **0**.

Included Linux Control, Windows build/Desktop security/MSIX, linux-x64/arm64 release archives, Ubuntu 22.04/24.04 native VMs, and Debian 12/13 systemd restart matrices.

Evidence: `POST_MERGE_CI.txt` and `POST_MERGE_*.log`.

Final main SHA `d645812d29d077e9a4dee1596ef09a70dc138090` after PR #17:

- workflows: **3/3 success**;
- jobs: **12/12 success**;
- non-green: **0**.

Evidence: `DELIVERY_REPAIR_POST_MERGE_CI.txt` and `DELIVERY_REPAIR_POST_MERGE_*.log`.

## Physical acceptance

Not run. Inputs are absent:

- `HUB_SSH_HOST=UNSET`
- `HUB_SSH_USER=UNSET`
- `SOURCE_SSH_HOST=UNSET`
- `SOURCE_SSH_USER=UNSET`
- `HOME_WG_IP=UNSET`
- `SECOND_WG_IP=UNSET`
- `SSH_IDENTITY_FILE=UNSET`

This remains a factual external acceptance blocker; see `PHYSICAL_INPUT_STATUS.txt`.
