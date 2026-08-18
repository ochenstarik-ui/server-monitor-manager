# B-3R Linux verification snapshot

Дата: 2026-08-04

## WSL2 Ubuntu 26.04

- .NET SDK: `10.0.302`
- Runtime: `10.0.10`
- `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`
- Worktree: `C:\Users\Ochenstarik\projects\server-monitor-manager-task3-b3`

## Windows verification (parent orchestrator)

| Проверка | Результат |
|---|---|
| Control Release tests | **93/93 PASS** |
| Bootstrap contract | **PASS** (`BOOTSTRAP_CONTRACT=PASS`) |
| `bash -n` helper/bootstrap/acceptance | **PASS** |
| Desktop x64 Release | **0 warnings, 0 errors** |
| Windows Desktop contracts | **PASS** (`Windows desktop contracts passed.`) |
| `git diff --check` | **PASS** |

## Linux verification (clean WSL snapshot)

Снапшот исходников был скопирован в `/tmp/hermes-b3r-linux.Qx5SYwBt` и затем удалён при сбросе WSL session.

| Проверка | Результат |
|---|---|
| Control Release tests (clean snapshot) | **93/93 PASS** |
| Restore warnings | `NETSDK1188` (invalid locale `zh-hant` etc. в сторонних пакетах) — не причина failure |

## Linux PublishTrimmed

| Конфигурация | Результат |
|---|---|
| `--self-contained false` | **FAIL** `NETSDK1102: Optimizing assemblies for size is not supported for the selected publish configuration` |
| `--self-contained true` | **PASS** (publish log saved) |
| Executable generated | `ochenstarik-smm-control` (78,256 bytes) |
| Executable startup (`backup-create`) | **NOT VERIFIED** — snapshot and executable were lost when WSL session ended |

## Trimming warnings

`IL2026` warnings present in `ProvisioningStore.cs`, `ProvisioningBaseInstallPlanStore.cs`, `ProvisioningFactsStore.cs` — pre-existing reflection-based JSON serialization in Provisioning paths, not in B-3 Link/orphan audit paths.

## Orphan audit runtime verification

Not completed before snapshot loss. The `TRIMMED_ORPHAN_AUDIT_VERIFIER.log` contains the last failed attempt (MissingMethodException for `ControlOptions.set_DatabasePath` in external host), which was subsequently addressed by adding `[DynamicDependency]` to `ControlOptions`, but the executable runtime verification was not rerun after the snapshot was lost.

## Conclusion

B-3R Linux Control suite passes on a clean snapshot. PublishTrimmed self-contained compilation succeeds. Runtime execution of the trimmed native executable and orphan audit verification remain pending due to WSL session loss.
