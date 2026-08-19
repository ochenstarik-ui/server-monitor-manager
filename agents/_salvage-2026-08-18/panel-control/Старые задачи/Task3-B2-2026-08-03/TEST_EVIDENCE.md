# Test evidence — Task 3 / B-2

Evidence date: `2026-08-03`.

## Local pre-merge

- Control Release suite: **76/76 passed**, 0 failed, 0 skipped.
- Desktop Security Release suite: **12/12 passed**, 0 failed, 0 skipped.
- Desktop Release x64 build: **0 warnings, 0 errors**.
- `bash -n` for helper, emergency, bootstrap contract and acceptance harness: **PASS**.
- Bootstrap/helper contract: `BOOTSTRAP_CONTRACT=PASS`.
- Windows Desktop contract: `Windows desktop contracts passed.`
- `git diff --check`: **PASS**.
- Fresh OS-safe ad-hoc verification: `HERMES_AD_HOC_TASK3_CHECKPOINT=PASS`.

Focused reconciliation evidence additionally covered:

- marker created immediately after a normal pass triggers a prompt pass;
- completion of observed generation A cannot consume replacement generation B;
- marker does not bypass typed firewall-unavailable backoff;
- unchanged second pass performs no connect/disconnect mutation;
- unavailable firewall produces one aggregate event and no later mutations;
- acceptance factual mismatch can retry and later converge without terminating the shell.

## Independent review

Full authoritative Task 3 contract and all 21 changed/new files were reviewed against base `89ef2fd9d3e596777fba47e4a17e181e7e788993`.

Verdict: **APPROVE**, with 0 BLOCKING, 0 HIGH, 0 MEDIUM and 0 LOW findings.

## GitHub CI — PR #12

All **14/14** reported checks passed:

- Linux control and agent: 2 × `build-and-test`;
- Windows build: 2 × `build`;
- release archives: linux-x64 and linux-arm64;
- Debian 12/13 x64/arm64 systemd restart;
- Ubuntu 22.04/24.04 x64/arm64 native VM.

Exact check names, durations and URLs are saved in `CI_CHECKS.txt`.

## Post-merge main

Verified at `b11c277ac7f79a18670932eca4622982d9ff48e0`:

- Control Release: **76/76 passed**.
- Desktop Release x64: **0 warnings, 0 errors**.
- Desktop Security command: exit **0**; pre-merge counted run was 12/12 on the identical reviewed tree.
- Bootstrap/helper contract: **PASS**.
- Windows Desktop contract: **PASS**.
- shell syntax and `git diff --check`: **PASS**.

## Patch verification

`Task3-B2.patch` represents exact base `89ef2fd…` → merge `b11c277…` content and is apply-tested against a clean detached base worktree. Result is recorded in `PATCH_VERIFICATION.txt`.

## Not performed

Physical three-server acceptance was **not run** because all required SSH/topology inputs were unset. Status remains **verified and merged; physical acceptance pending**.
