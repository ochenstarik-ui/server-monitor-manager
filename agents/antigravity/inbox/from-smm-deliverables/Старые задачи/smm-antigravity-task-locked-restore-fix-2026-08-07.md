# Починить PR #15: аналайзеры против `dotnet format` и RID-чувствительность lock-файлов

## Ветка

- Репозиторий: https://github.com/ochenstarik-ui/server-monitor-manager
- Ветка: `antigravity/reproducible-builds`, PR #15, HEAD `5ca4832`
- Рабочая папка: `C:\Users\Ochenstarik\projects\smm-antigravity`

Новую ветку не создавать — доделывается существующая.

## Состояние

PR #15 открыт с красным CI: `build` падает дважды, `build-and-test` падает дважды. В описании PR при этом стоит «Verified locally» и «covered by windows-build CI» — ровно тот job, который и падает.

Дефекты вызваны самим PR, оба воспроизводимы, оба имеют неочевидную причину. Разбор ниже.

---

## Дефект 1 — включение аналайзеров ломает проверку форматирования

`build-and-test`, шаг `Verify formatting`, exit code 2:

```
warning CA1848: For improved performance, use the LoggerMessage delegates …
    LinkReconciliationBackgroundService.cs(31,17), (78,21), (91,17)
    ControlMaintenance.cs(34,21), (46,17), (74,21), (92,17), (140,13), (212,9)
warning CA1865: Use 'string.StartsWith(char)' instead of 'string.StartsWith(string)'
    TimezoneProvisioningExecutor.cs(76,42)
##[error]Process completed with exit code 2
```

Причина: `Directory.Build.props` включил `EnableNETAnalyzers` и `AnalysisLevel=latest-recommended`. `dotnet format --verify-no-changes` проверяет не только форматирование, но и диагностики аналайзеров, и завершается ненулевым кодом при любом их наличии.

`TreatWarningsAsErrors` был намеренно исключён из задания, чтобы не ломать сборку — но этого оказалось недостаточно: гейт форматирования падает и от обычных warning'ов.

### Что решить

Аналайзеры и форматирование — разные проверки, и смешаны они в одном шаге. Выбрать один подход и обосновать выбор в отчёте:

**Предпочтительно.** Разделить проверки: `dotnet format whitespace --verify-no-changes` и `dotnet format style --verify-no-changes` в CI, а диагностики аналайзеров оставить в сборке как warning'и. Тогда гейт проверяет то, ради чего заведён, а аналайзеры дают сигнал, не блокируя.

**Допустимо.** Оставить `dotnet format` как есть, но явно подавить конкретные правила в `.editorconfig` с указанием причины для каждого. Тогда список подавленных правил приводится в отчёте с обоснованием по каждому — CA1848 про `LoggerMessage`-делегаты в некритичном по производительности пути это одно, а произвольное глушение всего семейства CA — другое.

**Недопустимо.** Отключить `EnableNETAnalyzers` или понизить `AnalysisLevel`, чтобы CI позеленел: это отменяет смысл изменения.

---

## Дефект 2 — lock-файлы не учитывают runtime identifier

`build` (Windows), шаг `Restore`:

```
error NU1004: The project's runtime identifiers have changed from.
Project's runtime identifiers: win-x64, lock file's runtime identifiers .
The packages lock file is inconsistent with the project dependencies
so restore can't be run in locked mode.
    ServerMonitorManager.Core.csproj [via ServerMonitorManager.Desktop.csproj]
```

Причина: `packages.lock.json` фиксирует не только версии, но и набор runtime identifier'ов. Файлы сгенерированы обычным `dotnet restore` без `-r`, поэтому список RID в них пуст. `windows-build.yml` восстанавливает с `-r win-x64`, и `--locked-mode` справедливо отвергает несоответствие.

Локальная проверка это не поймала: без `-r` восстановление проходит, что и отражено в описании PR как «5 projects restored».

### Что решить

Проект публикуется под три RID: `win-x64`, `linux-x64`, `linux-arm64`. Варианты:

**Предпочтительно.** Объявить `<RuntimeIdentifiers>` в проектах, которые публикуются под конкретный RID — `Control`, `Agent`, `Provisioning.Helper`, `Desktop` и транзитивно `Core`, — и перегенерировать lock-файлы так, чтобы они содержали все три RID. После этого `--locked-mode` работает во всех сценариях восстановления.

**Допустимо.** Отдельные lock-файлы под RID, если предыдущий вариант даёт неприемлемый рост файлов.

**Недопустимо.** `--force-evaluate`, снятие `--locked-mode` там, где он падает, или удаление `RestorePackagesWithLockFile`: это возвращает исходную невоспроизводимость, ради устранения которой задача и заведена.

---

## Дефект 3 — гарантия не покрывает то, что реально выпускается

`--locked-mode` добавлен только в `linux-control-agent.yml` и `windows-build.yml` — то есть ровно в те два workflow, которые видны на pull request. Артефакты, которые уходят пользователям, собираются в `linux-release.yml`, `windows-release.yml` и `linux-platform-matrix.yml`, и там восстановление по-прежнему свободное.

Это тот же структурный промах, что и в PR #14 с `permissions`: проверенным оказалось то, что видно на PR, а непроверенным — путь выпуска.

Добавить `--locked-mode` во все шаги восстановления и публикации во всех пяти workflow. Именно там воспроизводимость и имеет смысл: подпись артефакта без неё ничего не гарантирует.

Заметьте, что `linux-platform-matrix.yml` и release-workflow публикуют с `--runtime linux-x64` и `linux-arm64` — то есть дефект 2 обязан быть закрыт до этого шага, иначе упадут и они.

---

## Критерий приёмки

- `build` и `build-and-test` зелёные на PR #15;
- `--locked-mode` присутствует во всех шагах восстановления и публикации всех пяти workflow;
- продемонстрирована работа гарантии: намеренная правка одного lock-файла во временном коммите даёт красный прогон **на RID-специфичном** восстановлении, затем коммит откатывается — со ссылкой на красный прогон;
- версии пакетов не изменились относительно `main` — показать сравнение;
- выбранный подход по дефекту 1 обоснован; если правила подавлены, каждое перечислено с причиной;
- `git diff --name-only main` не содержит файлов из списка границ прошлого задания: `LinkService.cs`, `LinkPolicyApplier.cs`, `LinkReconciliationBackgroundService.cs`, `ControlStore.cs`, `ControlMaintenance.cs`, `ControlOptions.cs`, `appsettings.json`, `src/ServerMonitorManager.Provisioning.Helper/**`, `deploy/**`, `CertificateAuthority.cs`, `CertificateLifecycleService.cs`, `AgentClient.cs`, содержимое файлов в `tests/**` кроме `.csproj`;
- `TreatWarningsAsErrors` по-прежнему не включён;
- PR не смержен.

## Отдельное требование к отчёту

Причина обоих дефектов — вывод сделан до того, как CI отработал. Формулировки «Verified locally» и «covered by windows-build CI» стояли в описании PR в тот момент, когда этот самый job был красным.

Поэтому: **описание PR обновляется после завершения CI, а не до.** В разделе «Verified in CI» приводятся ссылки на конкретные завершённые прогоны с их статусом. Если прогон красный — это указывается прямо, и PR помечается как черновик.

Отсутствие проверки — не дефект отчёта. Утверждение о проверке, противоречащее видимому статусу CI, — дефект, и он дороже самой ошибки в коде, потому что обесценивает все последующие отчёты.

## Порядок

Дефект 2 → дефект 3 → дефект 1. Первые два связаны: расширение `--locked-mode` на release-workflow невозможно, пока lock-файлы не знают о RID.
