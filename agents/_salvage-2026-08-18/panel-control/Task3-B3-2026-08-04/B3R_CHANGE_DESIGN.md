# B-3R change design

## Scope

Добавочный contract `B3R_SPEC.md` применяется вместе с полным исходным B-3. Block C исключён.

## Инварианты реализации

1. Full pass начинает с одного `link-list` и одного `ListEffectiveLinksAsync`.
2. При отсутствии mutations дополнительных privileged calls нет.
3. При наличии любого числа mutations выполняется ровно один общий финальный `link-list`.
4. Для каждого natural key финальный count обязан быть:
   - Active: ровно `1`;
   - Disabled или DB-less orphan: ровно `0`.
5. События успешной mutation и orphan audit формируются только после финальной factual verification.
6. Для orphan audit порядок: factual verification → source-generated audit → success event.
7. Итог каждого examined key принадлежит ровно одному классу: `Converged`, `Failed`, `Deferred`.
8. `Converged + Failed + Deferred == Examined`; нарушение — программная ошибка, а не молчаливый успех.
9. `PendingActivation` относится к `Deferred`; Deferred не удерживает generation marker. Marker удерживает только Failed.
10. Lock order остаётся: sorted node locks → current effective per-Link gate. Новых lock-классов нет.
11. `lookup_node_ip` принимает только production-shape active row с валидным IPv4; reserved/missing → exit 80; malformed status/IP → exit 78.
12. Foreign nft comments никогда не становятся trusted keys и не мутируются.

## Минимальный двухфазный full-pass design

- Phase A: initial factual snapshot; natural-key reconciliation under existing locks; actual helper mutations; собрать mutated keys и preliminary states (`PendingActivation`, helper failures).
- Phase B: если были mutation attempts, один final factual snapshot; exact-count verification всех затронутых keys; затем DB state/audit/success events.
- `ConvergeAsync` остаётся единственным convergence core: phase/mode передаётся явно; отдельная копия connect/disconnect/error logic не допускается.

## Обязательные regressions

- DB-less no-op disconnect → Failed.
- Persisted Disabled при отсутствующем Node → Disabled после list verification.
- Mixed six-policy invariant с Converged/Failed/Deferred.
- Failed удерживает marker; только Converged+Deferred marker потребляет.
- 10 последовательных PendingActivation passes не создают prompt hot-loop.
- k mutations → два `link-list`; no drift → один.
- production-shape reserved/active-invalid/missing helper fixtures.
- Linux process integration fake поддерживает fact-first `link-list` protocol.
- Published linux-x64 trimmed Control assembly выполняет orphan removal и пишет ожидаемый source-generated audit JSON.

## Verification gates

- Windows focused/full Control;
- bootstrap contracts и `bash -n`;
- Desktop x64 Release + Windows contracts;
- WSL Ubuntu Linux full Control suite;
- linux-x64 PublishTrimmed + ad-hoc orphan audit execution through published assembly;
- `git diff --check`;
- independent reviewer получает оба полных spec: B-3 и B-3R.

## External residual

Physical acceptance требует реальных SSH/topology inputs и остаётся pending независимо от локальных tests/CI.
