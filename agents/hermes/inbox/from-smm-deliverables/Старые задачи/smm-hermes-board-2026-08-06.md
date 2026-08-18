# Server Monitor Manager — доска заданий Hermes

Тексты заданий для доски Hermes (проект `server-monitor-manager`).
Папка вне репозитория: задания не попадают в git и не засоряют PR.

Репозиторий: https://github.com/ochenstarik-ui/server-monitor-manager
Локальный клон: `C:\Users\Ochenstarik\projects\server-monitor-manager`
Рабочая копия незавершённого блока: `C:\Users\Ochenstarik\projects\server-monitor-manager-task3-b3`

Устроено так же, как `C:\Users\Ochenstarik\kagent-tasks`, но задания хранятся здесь, в `server-monitor-manager-deliverables`.

## Соглашения

- один файл — одно задание, имя `smm-hermes-task-<слаг>-<дата>.md`;
- тело файла самодостаточно: воркер стартует без истории чата, поэтому внутри всегда есть репозиторий, ветка, состояние, «зачем», список работ и критерий приёмки;
- ссылки на нормативные документы даются как «`docs/<файл>.md`, раздел N», без пересказа;
- независимому review передаётся **полный текст задания**, а не только диф. На четырёх блоках подряд diff-only review пропускал целые невыполненные требования.

## Как поставить задание

```bash
hermes kanban create "<заголовок>" --body "$(cat <файл>.md)" --project server-monitor-manager --assignee worker-code --workspace worktree --branch hermes/<слаг> --priority <N> --idempotency-key <уникальный-ключ> --created-by claude
```

Зависимость: `--parent <task_id>` при создании либо `hermes kanban link <parent> <child>` позднее.

## Текущая очередь

| Файл | Задание | Приоритет | Зависимость |
|---|---|---|---|
| `smm-hermes-task-b3r-2026-08-06.md` | Завершить B-3R: пакетная верификация факта, класс `Deferred`, Linux-прогон | 100 | — |
| `smm-hermes-task-cert-lifecycle-2026-08-06.md` | Жизненный цикл сертификатов: срок, автопродление, ротация CA | 90 | нет, файлы свободны — можно параллельно |

Эти два задания не пересекаются по файлам и идут в разных worktree одновременно.

## Очередь после B-3R

Тексты будут написаны, когда B-3R смержен — раньше они протухнут.

1. **Роль Monitor в bootstrap.** `install-monitor`, forced-command скрипт, контрактный тест формата снимка. Сейчас SSH-мониторинг не имеет серверной части вообще.
2. **Подписанная поставка.** Manifest v2, подпись cosign/minisign, публичный ключ вшит в bootstrap, отказ при несовместимых версиях, негативные тесты.
3. **Инженерная оснастка.** `Directory.Build.props`, central package management, lock-файлы, разделение тест-проектов.
4. **Гигиена репозитория.** Пиннинг Actions по SHA, dependabot, `SECURITY.md`, CHANGELOG.

Полный план и обоснование очерёдности: `smm-codex-plan-2026-08-04.md` в этой же папке; разбивка по горизонтам — `docs/product-horizons.md` в репозитории.

## Гейт

Горизонт 1 (provisioning-модули, firewall, alerts, Telegram, Docker, backups) не начинается, пока не закрыт Горизонт 0 целиком, включая физический acceptance на реальной тройке серверов. Последнее не может закрыть ни один исполнитель — нужны `HUB_SSH_HOST`, `HUB_SSH_USER`, `SOURCE_SSH_HOST`, `SOURCE_SSH_USER`, `HOME_WG_IP`, `SECOND_WG_IP`, `SSH_IDENTITY_FILE`.

## Статус

```bash
hermes kanban list
hermes kanban diag
```
