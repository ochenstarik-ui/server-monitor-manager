# KAgent — задания

Тексты заданий для доски Hermes (проект `kagent`, доска `default`).
Папка вне репозитория: задания не попадают в git и не засоряют PR.

Репозиторий: https://github.com/ochenstarik-ui/kagent
Локальный клон Hermes-проекта: `C:\Users\Ochenstarik\kagent`

## Соглашения

- один файл — одно задание, имя `<этап>-<короткий-слаг>.md`;
- тело файла самодостаточно: воркер стартует без истории чата, поэтому внутри
  всегда есть репозиторий, зависимости, «зачем», список работ и критерий приёмки;
- ссылки на решения даются в виде «ADR-00NN, ТЗ раздел N», без пересказа.

## Как поставить задание в Hermes

```bash
hermes kanban create "<заголовок>" --body "$(cat <файл>.md)" --project kagent --assignee worker-code --workspace worktree --branch wt/<слаг> --priority <N> --idempotency-key <уникальный-ключ> --created-by claude
```

`--idempotency-key` защищает от дублей при повторном запуске: если задание с
таким ключом уже есть и не архивировано, вернётся его id.

Зависимость между заданиями: `--parent <task_id>` при создании либо
`hermes kanban link <parent> <child>` позднее.

## Текущие задания

| Файл | Задание Hermes | ID | Приоритет |
|---|---|---|---|
| `0.9.0-green-trunk.md` | Зелёный trunk: CI, gateway, control-plane | `t_02a18136` | 100 |
| `0.9.1-measurability.md` | Вычисляемый статус, spec drift check, eval-suite | `t_e15a6516` | 90 |
| `0.9.2-reproducibility.md` | Кассеты, реестр промптов, жизненный цикл контекста | `t_4a957da4` | 85 |
| `0.9.3-verification-integrity.md` | Заморозка тестов, мутационная проверка, merge queue | `t_41b79fe7` | 80 |
| `0.9.4-economics.md` | Бюджетный ledger, автостоп, ledger эффектов, кэш | `t_9c52a43b` | 75 |
| `0.9.5-boundaries.md` | Контракт решения, уроки, личный контур | `t_9dfc2ce8` | 70 |

Задания 0.9.1–0.9.5 зависят от 0.9.0: пока сборка основной ветки красная,
их результат не проверяем.

## Статус

Проверить доску:

```bash
hermes kanban list
```

Разобрать диагностику упавших задач:

```bash
hermes kanban diag
```
