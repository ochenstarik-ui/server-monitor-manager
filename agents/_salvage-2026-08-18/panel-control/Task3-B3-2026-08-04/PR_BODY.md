## Summary

Implements Block B-3 and mandatory additive B-3R for fact-first Link policy reconciliation.

- adds strict helper `link-list` factual inspection with managed-comment parsing and fail-closed exits;
- reconciles active, disabled, duplicate, orphan, and DB-less orphan rules from one initial factual snapshot;
- preserves exact batching: one list on no drift, one initial plus one shared final list when mutations are attempted;
- verifies all touched natural keys by exact multiplicity before state, audit, and success events;
- separates Converged, Failed, and Deferred results with exhaustive invariants;
- preserves generation marker, bounded retry/throttle, and firewall-unavailable semantics;
- adds lock-safe batch finalization with natural-key/version revalidation;
- adds source-generated orphan audit JSON and completed-policy retention;
- updates Desktop effective/drift filtering and PendingActivation presentation;
- extends Linux/bootstrap/Desktop/acceptance contracts.

Block C is not included.

## Security boundaries

- existing `deploy/ochenstarik-smm-policy-apply` remains the only privileged policy target;
- sudoers is unchanged;
- strict argv/arity and helper exit contracts are preserved;
- no shell interpolation or broadened privilege surface;
- Linux peer/enrollment/root-owned helper boundaries are unchanged.

## Verification

- Windows Control Release: **94/94 PASS**
- clean WSL/Linux Control: **94/94 PASS**
- bootstrap contracts: **PASS**
- helper and acceptance script syntax: **PASS**
- Windows Desktop contracts: **PASS**
- Desktop x64 Release: **0 warnings / 0 errors**
- self-contained `linux-x64 PublishTrimmed`: **PASS**
- native published orphan audit: **PASS**
- native helper cardinality: **2× link-list, 1× link-disconnect**
- clean-base patch apply: **25/25 paths PASS**
- independent full B-3+B-3R security/spec review: **APPROVE**, no findings

## Physical acceptance

The mandatory three-server physical acceptance was not executed because all topology/SSH inputs are unavailable. It remains explicitly pending and is not replaced by tests, CI, or the native verifier.

Expected delivery status after green CI and merge: **merged / verified, physical acceptance pending**.
