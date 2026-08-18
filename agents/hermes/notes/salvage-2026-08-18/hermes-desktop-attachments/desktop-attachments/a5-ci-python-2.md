Правила работы: прочитай `C:\Users\Ochenstarik\kagent-tasks\hermes-work-protocol.md`.

Файлы: `.github/workflows/ci.yml`, новый `pyproject.toml` в корне, удаление одного
отслеживаемого бинарника.

## Дефект

В CI есть только jobs `node` и `rust`. Пять Python-сервисов — reasoning-engine,
agent-runtime, pipeline, observability, orchestrator, около 1700 строк — не проверяются
ничем. Скрипт `scripts/validate_repository.py` существует, но не вызывается.

Отдельно: файл `services/reasoning-engine/src/__pycache__/engine.cpython-311.pyc`
отслеживается git, хотя `.gitignore` содержит `__pycache__/` — правило добавили позже
файла.

## Что сделать

Добавить в `.github/workflows/ci.yml` job `python`:

- `ubuntu-latest`, Python 3.12, кеш pip;
- установка зависимостей всех сервисов из их `requirements.txt`;
- `ruff check services scripts`;
- `pytest` по каталогу `tests/` **с исключением интеграционных тестов**, требующих
  PostgreSQL: `tests/integration/test_pg.py` сейчас требует живую базу и в этой задаче не
  запускается;
- `python scripts/validate_repository.py`.

Создать корневой `pyproject.toml` с конфигурацией ruff: `target-version = "py312"`,
`line-length = 120`, правила `E`, `F`, `W`, `I`, `UP`, `B`.

Удалить `.pyc` из индекса: `git rm --cached <путь>`.

Jobs `node` и `rust` не менять.

## Ожидаемое препятствие

Ruff почти наверняка найдёт замечания в существующем коде — неиспользуемые импорты
(`asyncio`, `hashlib`, `signal`, `sys` в нескольких сервисах), порядок импортов, устаревшие
конструкции.

Правь только то, что ruff считает ошибкой, и только механически: удаление неиспользуемого
импорта, сортировка импортов, замена устаревшей конструкции. **Логику не менять.** Если
замечание требует изменения поведения — добавь точечное исключение с комментарием и опиши
в отчёте отдельной строкой.

## Критерий приёмки

Локально проходят:

```bash
ruff check services scripts
python scripts/validate_repository.py
```

Вывод приложить к отчёту. Файл workflow должен быть синтаксически корректным YAML —
проверь разбором, а не на глаз.
