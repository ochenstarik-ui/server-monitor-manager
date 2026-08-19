# PR #15: доделать, не отключая аналайзеры

## Состояние

Рабочая папка `C:\Users\Ochenstarik\projects\smm-antigravity`, ветка `antigravity/reproducible-builds`, PR #15, последний запушенный коммит `5ca4832` (CI красный). В рабочей копии есть незакоммиченные правки: `Directory.Build.props` и четыре `packages.lock.json`.

Разбор трёх дефектов — в задании `smm-antigravity-task-locked-restore-fix-2026-08-07.md`, оно остаётся в силе.

## Дефект 2 — закрыт правильно

`packages.lock.json` теперь содержат все три RID:

```json
"net10.0": {},
"net10.0/linux-arm64": {},
"net10.0/linux-x64": {},
"net10.0/win-x64": {}
```

Это предпочтительный вариант из задания. Сохранить.

## Дефект 1 — решается запрещённым способом

Незакоммиченная правка `Directory.Build.props`:

```diff
-    <EnableNETAnalyzers>true</EnableNETAnalyzers>
-    <AnalysisLevel>latest-recommended</AnalysisLevel>
```

В задании этот вариант назван недопустимым дословно: «Отключить `EnableNETAnalyzers` или понизить `AnalysisLevel`, чтобы CI позеленел: это отменяет смысл изменения». Аналайзеры — половина ценности `Directory.Build.props`; удалив их, PR перестаёт отличаться от простого переноса версий.

Вернуть обе строки и решить дефект 1 одним из разрешённых способов:

**Предпочтительно.** Разделить проверки в CI: `dotnet format whitespace --verify-no-changes` и `dotnet format style --verify-no-changes` вместо общего `dotnet format`. Тогда гейт проверяет форматирование, а диагностики аналайзеров остаются warning'ами в сборке и не блокируют.

**Допустимо.** Оставить общий `dotnet format`, но подавить конкретные правила в `.editorconfig`, перечислив каждое с причиной в отчёте. Сейчас мешают `CA1848` (LoggerMessage-делегаты, десять вхождений в `ControlMaintenance.cs` и `LinkReconciliationBackgroundService.cs`) и `CA1865` (`TimezoneProvisioningExecutor.cs:76`). Подавлять семейство целиком нельзя — только поимённо.

## Дефект 3 — не сделан

`--locked-mode` стоит только в двух явных шагах `dotnet restore`. Все `dotnet publish` восстанавливают неявно и без него:

- `linux-control-agent.yml` — строки 58, 61, 64, 67, 71, 74, 77;
- `linux-platform-matrix.yml` — строки 49, 52, 55;
- `linux-release.yml` — строки 82, 85, 88.

Это ровно те шаги, которые собирают артефакты, уходящие пользователям. Воспроизводимость, не покрывающая путь выпуска, не решает исходную задачу.

Добавить `--locked-mode` (либо `-p:RestoreLockedMode=true`) во все перечисленные шаги. Дефект 2 закрыт, поэтому RID-специфичная публикация больше не должна падать по NU1004 — но это надо подтвердить прогоном, а не рассуждением.

## Критерий приёмки

- `EnableNETAnalyzers` и `AnalysisLevel=latest-recommended` присутствуют в `Directory.Build.props`;
- `build` и `build-and-test` зелёные;
- `--locked-mode` действует во всех шагах восстановления и публикации всех пяти workflow;
- продемонстрирована работа гарантии: правка одного lock-файла во временном коммите даёт красный прогон **на RID-специфичной публикации**, затем коммит откатывается — со ссылкой на красный прогон;
- версии пакетов не изменились относительно `main`;
- если правила подавлены — каждое перечислено поимённо с причиной;
- `TreatWarningsAsErrors` по-прежнему не включён;
- границы прошлого задания соблюдены: `git diff --name-only main` не содержит `LinkService.cs`, `LinkPolicyApplier.cs`, `LinkReconciliationBackgroundService.cs`, `ControlStore.cs`, `ControlMaintenance.cs`, `ControlOptions.cs`, `appsettings.json`, `src/ServerMonitorManager.Provisioning.Helper/**`, `deploy/**`, `CertificateAuthority.cs`, `CertificateLifecycleService.cs`, `AgentClient.cs`, содержимого файлов в `tests/**` кроме `.csproj`;
- PR не смержен.

## Отчёт

Описание PR обновляется **после** завершения CI. Ссылки на конкретные завершённые прогоны с их статусом. Если прогон красный — сказать прямо и держать PR черновиком.

Отдельно объяснить выбор по дефекту 1: почему выбран именно этот из двух разрешённых вариантов.
