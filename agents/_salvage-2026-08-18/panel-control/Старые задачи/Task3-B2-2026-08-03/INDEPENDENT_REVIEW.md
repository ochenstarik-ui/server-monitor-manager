## APPROVE

Independent read-only repair review completed against base `89ef2fd9d3e596777fba47e4a17e181e7e788993`, the full authoritative Task 3 contract, all current tracked changes, and both untracked files.

### Findings

- **BLOCKING:** none
- **HIGH:** none
- **MEDIUM:** none
- **LOW:** none

No remediation is required in the reviewed diff.

### Prior findings

All findings from `deleg_18fa1344` are closed:

1. **Marker ignored regular throttle — CLOSED**
   - `LinkReconciliationBackgroundService.cs:33-39` maintains separate regular and backoff deadlines.
   - A valid marker generation bypasses only `_nextRegularAt`; it does not bypass `_backoffUntil`.
   - `LinkReconciliationTests.cs:142-166` verifies a marker created immediately after a normal pass triggers another prompt pass.

2. **Blind completion could remove a newer generation — CLOSED**
   - Emergency publication creates a UUID generation atomically under the shared root-owned flock.
   - `ochenstarik-smm-policy-apply:148-176` reads and completes generations under the same lock and unlinks only an exact generation match.
   - `LinkPolicyApplier.cs` strictly parses and passes the UUID.
   - Both the A→B unit regression and shell contract demonstrate that completing A preserves B.

3. **Acceptance retry exited on first mismatch — CLOSED**
   - `three-server-mesh.sh:52-99` separates nonfatal `probe_*` functions from fatal final `expect_*` assertions.
   - The restore loop retries factual status and actual reachable/blocked connectivity.
   - The bootstrap shell test proves an initial factual mismatch can later succeed.

4. **Configured Control with zero SSH profiles did not reconstruct banner — CLOSED**
   - `MainPage.xaml.cs:164-167` refreshes when either profiles exist or Control is configured.
   - `MainPage.xaml.cs:744-750` independently refreshes persisted Control state with zero SSH profiles.
   - Newest natural-key policy state reconstructs and clears the banner; the shared error is suppressed from individual rows.

5. **Localized nft stderr classification — CLOSED**
   - `ochenstarik-smm-policy-apply:4` forces `LC_ALL=C`.
   - Exit `79` plus exact stderr `mesh.firewall-unavailable` is required for the typed exception.
   - Unknown diagnostics remain generic fail-closed errors.

### Task 3 / B-2 DoD map

- Immediate startup and periodic all-effective-policy reconciliation: **implemented**
- Active rule restoration without heartbeat: **implemented and unit-covered**
- Disabled orphan removal plus `link.orphan-removed`: **implemented and covered**
- One aggregate unavailable event/state, zero later mutations: **implemented and covered**
- Emergency marker publication and prompt scheduling: **implemented and covered**
- Marker retention on unavailable, failure, cancellation, and cleanup failure: **implemented**
- Generation-safe marker completion: **implemented and covered**
- One factual convergence implementation for create, disable, reconnect, full pass, TTL, and retry: **confirmed**
- M1 `GetLinkAsync` and L1 readable CR/LF split: **fixed**
- Desktop durable banner reconstruction/recovery and row-error suppression: **implemented**
- Documentation at `docs/linux-bootstrap.md`: **aligned with implementation**
- Acceptance harness restore step: **implemented**, including factual and functional checks
- Physical acceptance: **PENDING**
- Linux real-process integration: **CI-required; not locally established in this Windows review**

### Boundaries and concurrency

- **Sudo/argv boundary:** strict. Control uses `ProcessStartInfo.ArgumentList`, `sudo -n`, and the fixed helper path; no shell interpolation was introduced. Helper actions enforce exact arity, UUID format, node IDs, protocol, port, and TTL.
- **Locking:** paths requiring both lock classes acquire sorted ordinal node locks before the per-Link gate. Bulk unavailable marking releases the current locks before reacquiring the established hierarchy. No inverted/new lock class was introduced.
- **Scheduling:** one pass gate prevents overlap; ordinary passes are interval-throttled; marker generations bypass only the regular deadline; shared failure uses bounded backoff.
- **Cancellation:** `OperationCanceledException` is preserved, and marker completion occurs only after a successful, failure-free pass.

### Scope decisions

- **M2, M4, M5:** explicitly deferred in `docs/roadmap.md` with bounded rationale. This satisfies the decision requirement but does not mark those debts complete.
- **Block C:** untouched; no Block C implementation is included in this diff.
- **Overall status:** candidate implementation is approved, but Block B/Task 3 must remain **merged/verified, physical acceptance pending**, not closed. Required topology inputs and the real `SMM_ACCEPT_RESTORE=1 SMM_ACCEPT_REBOOT=1` run are still absent.

### Verification

- `bash -n` for changed shell scripts: **PASS**
- `tests/bootstrap/test-bootstrap-contract.sh`: **PASS** (`BOOTSTRAP_CONTRACT=PASS`)
- `git diff --check`: **PASS**
- Final worktree status matched the initial reviewed modifications/untracked files.
- Local .NET rerun was unavailable because `dotnet` is not installed or discoverable on this worker. I therefore did not independently reproduce the supplied fresh 76/76 Control result; the orchestrator’s stated Control/Desktop/build evidence remains the source for those checks.

### Files

- **Created or modified by this review:** none.