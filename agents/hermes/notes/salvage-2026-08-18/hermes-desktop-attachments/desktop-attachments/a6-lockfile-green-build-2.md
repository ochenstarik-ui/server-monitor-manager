Правила работы: прочитай `C:\Users\Ochenstarik\kagent-tasks\hermes-work-protocol.md`.

Завершающая задача пакета A. Выполняется после A2, A4 и A5.

## Дефект

В репозитории нет `pnpm-lock.yaml`. Job `node` падает на шаге `setup-node` с `cache: pnpm`
ещё до установки зависимостей:

```
##[error]Dependencies lock file is not found in /home/runner/work/kagent/kagent.
Supported file patterns: pnpm-lock.yaml
```

Из-за этого `pnpm typecheck`, `pnpm test` и `pnpm build` не выполнялись в CI ни разу.
Отсутствие лок-файла нарушает и `docs/THREAT_MODEL.md`, где lockfiles указаны как контроль
против компрометации цепочки поставок.

## Что сделать

1. Слить ветки задач A2, A4 и A5 в свою ветку либо взять их изменения как базу — уточни у
   владельца, если ветки ещё не в `main`.
2. Сгенерировать лок-файл:

```bash
pnpm install --lockfile-only
```

3. Закоммитить `pnpm-lock.yaml`.
4. Прогнать полный набор проверок монорепозитория и добиться зелёного результата.

## Критерий приёмки

Все команды проходят, вывод каждой приложить к отчёту:

```bash
pnpm install --frozen-lockfile
pnpm typecheck
pnpm test
pnpm build
cargo fmt --manifest-path services/gateway/Cargo.toml --check
cargo clippy --manifest-path services/gateway/Cargo.toml --all-targets -- -D warnings
cargo test --manifest-path services/gateway/Cargo.toml
ruff check services scripts
python scripts/validate_repository.py
```

## Отдельно

Если `pnpm build` вскроет дефекты, не входящие в задачи A1–A5, — не чини их здесь. Заведи
карточку на каждый и сообщи. Задача считается выполненной, когда либо всё зелёное, либо
явно перечислено, что осталось красным и почему.

После вливания в `main` пункт roadmap **не отмечать**: статус этапа будет вычисляться из
артефактов CI, см. `docs/adr/0016-computed-stage-status.md`.
