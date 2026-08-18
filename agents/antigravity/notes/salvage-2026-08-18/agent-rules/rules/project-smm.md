---
trigger: always_on
---

# ServerMonitorManager: проектные правила

Это правило дополняет документацию workspace: `SECURITY.md`, `CONTRIBUTING.md`, `README.md` и текущий `TASK.md`. Файлов `AGENTS.md` и `GEMINI.md` в этом репозитории нет — не ссылаться на них и не выдумывать их содержимое. При конфликте применять более строгое ограничение.

## Контекст

- Стек: C# / .NET, решение `ServerMonitorManager.slnx`.
- Структура: `src/`, `tests/`, `build/`, `deploy/`, `docs/`.
- Стиль: `Directory.Build.props`; версии пакетов: `Directory.Packages.props`.

## Контракт изменений

- Добавлять код и тесты в соответствующие проекты `src/` и `tests/`.
- Не менять центрально управляемые версии пакетов в отдельных `.csproj`.
- Изменения `deploy/`, auth, secrets, remote execution, service control и привилегий считать complex/security-sensitive.
- Для новой логики добавлять тесты; не ослаблять существующие.

## Проверки

Выбрать точные команды из документации репозитория. Если особых команд нет, минимум:

```powershell
dotnet build ServerMonitorManager.slnx --no-restore
dotnet test ServerMonitorManager.slnx --no-build
```

Не утверждать, что они прошли, если restore или другая предпосылка не были выполнены. В таком случае статус — `BLOCKED` или `NOT_RUN` с причиной.
