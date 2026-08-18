# Server Monitor Manager — Task 3 / B-2

## Статус

**Verified and merged; physical acceptance pending.**

- PR: https://github.com/ochenstarik-ui/server-monitor-manager/pull/12
- Base: `89ef2fd9d3e596777fba47e4a17e181e7e788993`
- Reviewed implementation commit: `c757920791673dbc34c4a1e826caf16f6ba07da1`
- Squash merge commit: `b11c277ac7f79a18670932eca4622982d9ff48e0`
- Merged at: `2026-08-03T06:55:49Z`
- Scope: 21 files, 1113 insertions, 172 deletions

## Выполнено

- Добавлена немедленная и периодическая реконсиляция всех effective Link-политик независимо от heartbeat/reconnect.
- Все create/disable/reconnect/background/retry paths используют один factual convergence path с порядком locks `sorted endpoint node locks → per-Link gate`.
- Добавлен интервал `Control__LinkReconciliationSeconds`: default 300, диапазон 30–3600.
- Реализован typed global state `mesh.firewall-unavailable` с прекращением дальнейших firewall mutations в текущем pass и bounded backoff.
- Добавлен generation-aware root marker для `firewall-restore`/`mesh-enable`: atomic publish, `root:root 0600`, общий root-owned flock, exact-generation consume.
- Сохранён strict privileged-helper argv boundary; неизвестные nft errors остаются fail-closed; диагностика стабилизирована через `LC_ALL=C`.
- Desktop показывает единый глобальный firewall banner, восстанавливает его из persisted Link state и event stream, включая конфигурацию Control без SSH profiles.
- Исправлены M1 (CR/LF compaction) и L1 (direct Link lookup вместо repeated O(N) list scan).
- Acceptance harness расширен сценариями firewall restore и короткого Hub restart/reboot с factual и connectivity probes.

## Review

Независимый full-contract repair review: **APPROVE**.

- BLOCKING: 0
- HIGH: 0
- MEDIUM: 0
- LOW: 0

Полный verdict сохранён в `INDEPENDENT_REVIEW.md`.

## Отложенный scope

M2, M4 и M5 явно перенесены в отдельный B-3 PR. Block C не включён.

## Physical acceptance

Не выполнена: `HUB_SSH_HOST`, `HUB_SSH_USER`, `SOURCE_SSH_HOST`, `SOURCE_SSH_USER`, `HOME_WG_IP`, `SECOND_WG_IP`, `SSH_IDENTITY_FILE` отсутствуют.

CI, unit/integration/contract tests и mocks не заменяют physical acceptance. Для окончательного принятия требуется реальная topology и команда:

`SMM_ACCEPT_RESTORE=1 SMM_ACCEPT_REBOOT=1 tests/acceptance/three-server-mesh.sh`

До её успешного выполнения Block B/B6 не обозначается как accepted.
