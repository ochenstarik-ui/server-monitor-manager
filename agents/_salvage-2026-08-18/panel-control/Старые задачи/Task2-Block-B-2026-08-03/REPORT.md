# Server Monitor Manager — Task 2 / Block B

Дата снимка: 2026-08-03 10:37:11 +07:00

## Состояние

Пакет содержит merged результат Block B и локальное/CI/review evidence. Physical acceptance остаётся отдельным внешним evidence.

- Merge commit: `89ef2fd9d3e596777fba47e4a17e181e7e788993`
- База: `ba14d2921142652971a0dc78970c2213921069da`
- Изменено: 14 tracked-файлов, 551 additions / 108 deletions
- Feature commits: `172df91`, `1580ec7`
- PR: https://github.com/ochenstarik-ui/server-monitor-manager/pull/11
- PR: **MERGED**
- CI: **14/14 SUCCESS** на head `1580ec7`
- Финальный repair-review: **APPROVE**, без BLOCKING/HIGH/MEDIUM
- Physical acceptance: не выполнен — отсутствуют SSH/topology inputs

## Реализовано

- Строгий helper action `link-status` для factual nftables state.
- Различение отсутствующей nftables table и существующей table без matching rule.
- Connect/disconnect больше не считают exit code доказательством применения политики.
- После mutation выполняется factual probe; `ActualState` отражает обнаруженное состояние.
- Reconnect reconciliation приводит Links к desired state.
- Desktop показывает desired state, actual state, drift, last error и version.
- Three-server acceptance усилен factual checks после connect, disable, TTL и optional restart.
- Node/per-Link serialization закрывает reconnect ↔ certificate reenrollment race.
- Interrupted disable и reenrollment replays возобновляют kill-switch convergence.
- Completed stale Link replay не отключает более новый Link с тем же policy tuple.

## Независимый review

Первый review вернул `CHANGES_REQUESTED` с двумя HIGH:

1. Reenrollment находился вне общей serialization boundary.
2. Idempotency replay не возобновлял незавершённую privileged operation.

Оба findings исправлены. Добавлены regression tests. Полный исходный review сохранён в `INDEPENDENT_REVIEW_CHANGES_REQUESTED.txt`. Финальный repair-review сохранён в `INDEPENDENT_REPAIR_REVIEW_APPROVE.txt`; verdict: **APPROVE**, без BLOCKING/HIGH/MEDIUM.

## Содержимое пакета

- `block-b-current.diff` — полный binary-capable Git patch `ba14d29..89ef2fd`.
- `block-b-modified-files.zip` — snapshot 14 изменённых файлов с сохранением repo paths.
- `REPORT.md` — этот отчёт.
- `TEST_EVIDENCE.md` — выполненные проверки и ограничения.
- `INDEPENDENT_REVIEW_CHANGES_REQUESTED.txt` — полный первый независимый review.
- `INDEPENDENT_REPAIR_REVIEW_APPROVE.txt` — финальный независимый repair-review.
- `CI_CHECKS.txt` — итоговые PR #11 checks.
- `SHA256SUMS` — контрольные суммы артефактов.

## Ограничение physical acceptance

Не были предоставлены:

- `HUB_SSH_HOST`, `HUB_SSH_USER`
- `SOURCE_SSH_HOST`, `SOURCE_SSH_USER`
- `HOME_WG_IP`, `SECOND_WG_IP`
- `SSH_IDENTITY_FILE`

Contract/mock tests не выдаются за physical acceptance.
