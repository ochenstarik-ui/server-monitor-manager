# INDEPENDENT REVIEW — Block B-3 + B-3R

## VERDICT: **REQUEST_CHANGES**

### Summary

Most B-3/B-3R requirements are implemented correctly, including fact-first batching, exhaustive result buckets, helper validation, marker throttling, source-generated orphan audit serialization, retention/UI behavior, and Linux protocol-test shape.

However, the suspected finalization race is real and blocking. `ReconcileAllAsync` releases the existing node and Link locks before obtaining the shared final factual snapshot and finalizing staged mutations. `FinalizeBatchAsync` then writes state and emits success/failure telemetry from stale `LinkPolicy` objects without reacquiring locks or re-reading the current effective policy by natural key.

---

## BLOCKING

### B1. Batched finalization escapes the required lock/natural-key boundary and can overwrite newer operations

**Evidence**

- The mutation phase holds sorted node locks and the per-Link gate only inside each loop iteration:
  - `src/ServerMonitorManager.Control/LinkService.cs:195-205`
  - `src/ServerMonitorManager.Control/LinkService.cs:255-260`
  - orphan path: `src/ServerMonitorManager.Control/LinkService.cs:271-279`
  - orphan locks released: `src/ServerMonitorManager.Control/LinkService.cs:329-334`
- Only after all those locks have been released does the pass take its final factual snapshot and finalize:
  - `src/ServerMonitorManager.Control/LinkService.cs:342-354`
- Staged entries retain the earlier `LinkPolicy`, expected state, and persistence flag:
  - `src/ServerMonitorManager.Control/LinkService.cs:798-818`
- `FinalizeBatchAsync` trusts that staged object and the shared snapshot; it neither reacquires locks nor calls `GetEffectiveLinkAsync`:
  - `src/ServerMonitorManager.Control/LinkService.cs:566-600`
- Successful finalization writes by the staged row ID and publishes events:
  - `src/ServerMonitorManager.Control/LinkService.cs:605-632`
- Failed finalization also writes by the staged row ID and publishes failure:
  - `src/ServerMonitorManager.Control/LinkService.cs:636-650`
- The store update is unconditional by ID—there is no expected version, desired-state predicate, or natural-key/effectiveness guard:
  - `src/ServerMonitorManager.Control/ControlStore.cs:1337-1373`
- A correct natural-key lookup exists but is not used during finalization:
  - `src/ServerMonitorManager.Control/ControlStore.cs:1412-1433`

**Concrete races**

1. **Concurrent Disable**
   - Reconciliation stages an `Active` policy after applying `link-connect`.
   - It releases node/Link locks.
   - `DisableAsync` acquires the locks, changes desired state to `Disabled`, removes the rule, verifies it, and reports success.
   - The final reconciliation list now sees zero:
     - reconciliation calls `FailConvergenceAsync` with the stale expectation `Active`;
     - the same row becomes `ActualState=Failed` even though its current desired state is `Disabled` and factual state is correctly disabled;
     - a stale `link.failed` event/audit is produced.
   - In another interleaving, the final list sees one before Disable removes it, but Disable completes before `FinalizeBatchAsync`; reconciliation then writes `ActualState=Active` over the completed disable and emits `link.active`/`link.reapplied`.

2. **Concurrent Create for the same natural key**
   - Reconciliation stages removal of a rule corresponding to an older disabled policy.
   - Locks are released.
   - Create inserts a newer effective `Active` version and reconnects the rule.
   - If the final list sees the new rule, reconciliation marks the old staged row `Partial` and classifies the pass as failed even though the current effective policy is factually converged.
   - If the final list occurs before Create reconnects, but finalization occurs afterward, reconciliation can record `link.orphan-removed` audit and publish success against a natural key that is now intentionally active.

3. **Concurrent reconnect/node reconciliation**
   - `ReconcileLinksForNodeAsync` uses the same node locks and Link gate at `LinkService.cs:121-155`.
   - Since batch finalization occurs outside them, its newer factual/state result can be overwritten or contradicted by stale full-pass finalization.

**Impact**

- Stale `ActualState` writes.
- Incorrect `link.active`, `link.reapplied`, `link.orphan-removed`, `link.failed`, or `link.partial` events.
- Incorrect orphan audit records.
- `Converged`/`Failed` classification against an obsolete desired version.
- Possible emergency-marker retention or consumption based on a stale classification.
- Direct violation of B-3’s “sorted node locks → per-Link gate” and current/effective natural-key contract.

**Required correction**

The final factual snapshot and all staged finalization must remain inside an existing synchronization boundary that excludes Create/Disable/Reconnect for every staged natural key. Before writing or publishing:

1. reacquire the relevant sorted node locks and Link gates in the prescribed order;
2. re-read the current effective policy by natural key;
3. verify that identity/version/desired state still match the staged operation;
4. classify superseded work from the current effective desired state rather than updating the stale row;
5. keep the final factual snapshot valid through DB finalization and success telemetry.

A global new lock class would conflict with the specification; this should be solved using the existing node/per-Link lock model and natural-key revalidation.

---

## HIGH

### H1. Required concurrency regression coverage is missing

The only new concurrency-oriented test adopts an orphan while reconciliation is blocked around the **initial** listing:

- `tests/ServerMonitorManager.Control.Tests/LinkReconciliationTests.cs:467-491`

That test releases its held node locks before the reconciliation mutation/finalization path proceeds. It does not place a Create, Disable, or Reconnect operation in the window between:

1. staged mutation,
2. lock release,
3. final `link-list`,
4. `FinalizeBatchAsync`.

Missing deterministic tests:

- Disable after an active repair is staged but before the final list.
- Disable after the final list but before DB/event finalization.
- Create a newer active policy for a staged disabled/orphan natural key before the final list.
- Create after the final list but before orphan audit/event finalization.
- Node reconnect during the same finalization window.
- Assertions that no stale actual-state write, success/failure event, audit, or result classification survives these interleavings.

These tests are required to close B1 and prevent recurrence.

### H2. B-3R’s published-trimmed orphan-path runtime verification is not established

The orphan test validates audit JSON inside the ordinary test process:

- `tests/ServerMonitorManager.Control.Tests/LinkReconciliationTests.cs:109-147`

The implementation correctly uses a named DTO and source-generated context:

- `src/ServerMonitorManager.Control/ControlJsonContext.cs:5-13`
- `src/ServerMonitorManager.Control/ControlStore.cs:1376-1398`

Audit persistence also precedes the final success event:

- `src/ServerMonitorManager.Control/LinkService.cs:625-631`

Nevertheless, B-3R explicitly requires executing the orphan path in the published `linux-x64 PublishTrimmed` artifact and checking the resulting audit record. The supplied deterministic evidence establishes that a self-contained trimmed artifact **builds**, not that this runtime path was executed. This acceptance evidence remains missing or was not supplied to the reviewer.

---

## MEDIUM

None beyond the blocking/high findings above.

---

## LOW

None.

---

## Requirements verified as correctly represented

- **Exact B-3R batching:** no-drift path performs one initial `ListRulesAsync`; any attempted mutation causes one shared final list:
  - `LinkService.cs:164-181`
  - `LinkService.cs:342-354`
  - test: `LinkReconciliationTests.cs:58-106`
- **Result buckets and invariant:** `Examined = Converged + Failed + Deferred` is enforced for both result types:
  - `ControlStore.cs:1774-1806`
  - `LinkService.cs:822-857`
- **Deferred semantics:** `PendingActivation` maps to `Deferred`; only `Failed` blocks marker completion:
  - `LinkService.cs:235-249`
  - `LinkReconciliationBackgroundService.cs:69-85`
- **Prompt throttle:** three failures restore regular throttling, marker is retained, warning is emitted once:
  - `LinkReconciliationBackgroundService.cs:40-45`
  - `LinkReconciliationBackgroundService.cs:74-93`
  - `LinkReconciliationBackgroundService.cs:102-112`
- **Firewall-unavailable initial fail-stop:** initial `link-list` failure precedes mutation and produces the aggregate path:
  - `LinkService.cs:164-174`
- **Final-list firewall-unavailable handling:** staged success events are not finalized when final inspection fails:
  - `LinkService.cs:342-353`
- **Mandatory factual interfaces:** `ListRulesAsync` and raw `ApplyDisconnectAsync(LinkRule, …)` have no fail-open default bodies:
  - `LinkPolicyApplier.cs:7-13`
- **Reserved/malformed nodes:** four-field record is required; non-active valid status returns 80; malformed status and invalid active IP return 78:
  - `deploy/ochenstarik-smm-policy-apply:50-63`
  - production-shaped tests: `tests/bootstrap/test-bootstrap-contract.sh:142-192`
- **Managed-rule parsing/security boundary:** foreign comments are ignored; malformed managed comments are diagnosed; source/target/protocol/port are validated:
  - `deploy/ochenstarik-smm-policy-apply:158-189`
- **Helper exits:** missing firewall is 79 with exact marker; unknown inspection errors remain 78; missing mesh directory makes `reconcile-status` complete:
  - `deploy/ochenstarik-smm-policy-apply:94-115`
  - `deploy/ochenstarik-smm-policy-apply:205-220`
- **Generation-safe marker completion:** compare-and-remove occurs under the helper lock:
  - `deploy/ochenstarik-smm-policy-apply:223-237`
- **Linux integration shape:** fake helper supports fact-first `link-list`, asserts one list for no drift and initial+mutation+final list for drift/orphan:
  - `LinkPolicyApplierIntegrationTests.cs:16-23`
  - `LinkPolicyApplierIntegrationTests.cs:66-77`
  - `LinkPolicyApplierIntegrationTests.cs:104-146`
- **Retention:** configurable/validated, only current completed `Disabled/Disabled` natural keys older than cutoff are removed with their history:
  - `ControlOptions.cs:30`
  - `Program.cs:34-54`
  - `ControlStore.cs:355-388`
- **Desktop filtering and Deferred presentation:**
  - `LinksPage.xaml.cs:22-41`
  - `MeshModels.cs:65-74`
- **No Block C:** no Block C implementation was found in the reviewed diff.

---

## Executed review evidence

- Read both authoritative specifications completely.
- Reviewed branch `hermes/task3-b3-fact-reconciliation` at base/HEAD `b11c277ac7f79a18670932eca4622982d9ff48e0`.
- Inspected all changed paths and the untracked `ControlJsonContext.cs`.
- `git diff --check b11c277...` — **PASS**.
- `bash tests/bootstrap/test-bootstrap-contract.sh` — **PASS** (`BOOTSTRAP_CONTRACT=PASS`).
- `bash -n tests/acceptance/three-server-mesh.sh` — **PASS**.
- `bash -n deploy/ochenstarik-smm-policy-apply` — **PASS**.
- `powershell ... tests/windows/Test-DesktopContracts.ps1` — **PASS**.
- Local `dotnet test` could not run because `dotnet` is unavailable in this reviewer environment. Parent-provided 93/93 Windows and clean-WSL results were treated as evidence, not independently reproduced.
- Final status was rechecked; the review created or modified **no files**.