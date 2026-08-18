# Воспроизводимость сборки: централизованные версии пакетов и lock-файлы

## Часть 1 — сначала исправить PR #14

Не начинать новую ветку, пока не закрыты два дефекта в уже открытом PR #14 (`antigravity/repo-hygiene`). Оба находятся в release-workflow, которые **не запускаются на pull request** — они триггерятся только по тегу и `workflow_dispatch`, поэтому зелёный CI на PR их не покрывает.

### 1.1. `permissions` стоит на уровне шага — это недопустимый ключ

Три места: `linux-release.yml:63`, `linux-release.yml:127`, `windows-release.yml:86`.

```yaml
      - name: Attach bootstrap to GitHub Release
        uses: softprops/action-gh-release@3bb1273…
        permissions:          # ← уровень шага
          contents: write
        with:
```

В схеме GitHub Actions `permissions` существует только на уровне workflow и на уровне job. У шага такого ключа нет. При срабатывании по тегу workflow либо не загрузится с ошибкой `Unexpected value 'permissions'`, либо шаг выполнится с унаследованным `contents: read` и публикация релиза упадёт с 403.

Исправление: перенести `permissions: contents: write` на уровень job. В `linux-release.yml` это оба job — `bootstrap` и `publish`; в `windows-release.yml` — `package`. Workflow-уровень оставить `contents: read`.

Job-уровень — самая мелкая гранулярность, которую даёт GitHub; более узко не бывает.

### 1.2. SBOM генерируется и выбрасывается

Три места: `linux-release.yml:57`, `linux-release.yml:111`, `windows-release.yml:67`.

Файл SBOM не попадает ни в `path:` шага `upload-artifact`, ни в `files:` шага `action-gh-release`. То есть он создаётся и остаётся в рабочем каталоге runner'а. Требование задания «приложить к артефактам релиза» не выполнено.

Сопутствующее:

- `|| true` гасит любую ошибку генерации: сломанный шаг выглядит зелёным, а SBOM молча отсутствует. Для артефакта цепочки поставки это обесценивает саму затею — убрать;
- в job `bootstrap` файла `linux-release.yml` нет шага `setup-dotnet`, а SBOM пишется в подкаталог `sbom/`, тогда как остальные пути в этом job — корневые;
- SBOM в job `bootstrap` и в job `publish` строится из одного и того же `ServerMonitorManager.slnx`, то есть дублируется.

Исправление: генерировать SBOM один раз, до шага загрузки, в job, где уже настроен .NET; добавить файл и в `upload-artifact`, и в `files:` релиза; `|| true` убрать. Дублирующую генерацию в job `bootstrap` удалить.

### 1.3. Как проверить себя

Release-workflow нельзя прогнать через PR. Проверять так:

- `actionlint` на все пять файлов `.github/workflows/` — он ловит и недопустимые ключи, и опечатки в схеме;
- либо `gh workflow run` вручную по `workflow_dispatch` на своей ветке, если это не приведёт к публикации релиза (шаги релиза защищены `if: startsWith(github.ref, 'refs/tags/')`, поэтому на ветке они пропускаются, а разбор файла всё равно произойдёт).

В отчёте показать вывод проверки. Утверждение «исправлено» без демонстрации разбора файла не принимается — именно отсутствие такой проверки и привело к дефекту.

---

## Часть 2 — новая задача

### Репозиторий и ветка

- Репозиторий: https://github.com/ochenstarik-ui/server-monitor-manager
- База: `main` (после merge PR #13 и #14)
- Ветка: `antigravity/reproducible-builds`
- Рабочая папка: `C:\Users\Ochenstarik\projects\smm-antigravity`

### Зачем

`docs/installer-contract.md` §1 требует, чтобы production-установка использовала закреплённый релиз, а `docs/product-horizons.md`, Горизонт 0, требует подписанной поставки с хэшами всех артефактов. Обе гарантии опираются на предположение, что из одного коммита получается один и тот же бинарник.

Сейчас это неверно: версии NuGet-пакетов заданы в каждом `.csproj` по отдельности, lock-файлов нет, восстановление зависимостей идёт по диапазонам. Сборка одного и того же тега завтра может дать другой набор зависимостей, и никакая подпись артефакта этого не покажет.

Задача закрывает предпосылку, без которой подписанная поставка не имеет смысла.

### Работы

**1. `Directory.Packages.props` в корне.**

Централизованное управление версиями: `ManagePackageVersionsCentrally` включено, все `PackageVersion` собраны в одном файле, из `.csproj` версии убраны, остаются только `PackageReference` без атрибута `Version`.

Затрагиваются все проекты решения: `Core`, `Control`, `Agent`, `Desktop`, `Provisioning.Helper`, тестовый проект.

**2. Lock-файлы.**

`RestorePackagesWithLockFile` включён, `packages.lock.json` каждого проекта закоммичен. В CI восстановление выполняется с `--locked-mode`, чтобы расхождение lock-файла с проектом ломало сборку, а не молча подтягивало другую версию.

**3. `Directory.Build.props` в корне.**

- `Nullable`, `EnableNETAnalyzers`, `AnalysisLevel latest-recommended`;
- `InvariantGlobalization` там, где это уместно для серверных проектов;
- детерминированная сборка: `Deterministic`, `ContinuousIntegrationBuild` под условием `$(CI)`;
- `TreatWarningsAsErrors` **не включать** — см. границы.

**4. CI.**

В `linux-control-agent.yml` и `windows-build.yml` добавить восстановление в locked-режиме. Прогон должен падать, если lock-файл не соответствует проектам.

### Границы

Не изменять:

- `src/ServerMonitorManager.Control/LinkService.cs`, `LinkPolicyApplier.cs`, `LinkReconciliationBackgroundService.cs`, `ControlStore.cs`, `ControlMaintenance.cs`, `ControlOptions.cs`, `appsettings.json`;
- `src/ServerMonitorManager.Provisioning.Helper/**`, `deploy/**`;
- `CertificateAuthority.cs`, `CertificateLifecycleService.cs`, `src/ServerMonitorManager.Agent/AgentClient.cs`;
- содержимое файлов в `tests/**` — правки допускаются только в `.csproj` тестового проекта.

Первые две группы заняты заданием B-3R, третья — заданием по жизненному циклу сертификатов.

Не входит в задание:

- **`TreatWarningsAsErrors`.** Включение сейчас сломает незавершённую ветку `hermes/task3-b3-fact-reconciliation` при её ребейзе на main. Отдельная задача после её merge;
- разделение тестов на `Core.Tests` / `Control.Tests` / `Agent.Tests` / `Helper.Tests` — тестовые файлы заняты;
- обновление версий пакетов: задача переносит существующие версии как есть, а не апгрейдит их. Любое изменение номера версии должно быть отдельно обосновано в отчёте;
- merge собственного PR.

### Критерий приёмки

- ни один `.csproj` не содержит `Version=` в `PackageReference`;
- `packages.lock.json` закоммичены для всех проектов;
- CI восстанавливает зависимости в `--locked-mode` и падает при рассинхроне — продемонстрировать намеренной поломкой lock-файла в отдельном временном коммите, показать красный прогон, затем откатить;
- номера версий пакетов не изменились относительно `main` — показать сравнение;
- `git diff --name-only main` не содержит ни одного файла из списка границ;
- Control suite прогнан на Linux;
- Desktop собирается — в CI, локально WinUI 3 собрать нельзя, если нет Windows App SDK;
- CI зелёный, PR создан и не смержен.

### Отчётность

Раздельно: что проверено локально, что в CI со ссылками на прогоны, что не проверялось и почему.

Отдельно показать: вывод `actionlint` по части 1 и сравнение версий пакетов до и после по части 2.

Физический acceptance (`SMM_ACCEPT_RESTORE=1 SMM_ACCEPT_REBOOT=1 tests/acceptance/three-server-mesh.sh`) выполнить нельзя — не выданы SSH- и topology-параметры.
