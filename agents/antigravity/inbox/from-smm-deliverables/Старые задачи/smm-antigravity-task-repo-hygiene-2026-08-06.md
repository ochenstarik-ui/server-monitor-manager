# Гигиена репозитория и цепочки поставки

## Репозиторий и ветка

- Репозиторий: https://github.com/ochenstarik-ui/server-monitor-manager
- База: `main` @ `b11c277ac7f79a18670932eca4622982d9ff48e0`
- Ветка: `antigravity/repo-hygiene`
- Работать в **отдельном клоне или worktree**. Клон `C:\Users\Ochenstarik\projects\server-monitor-manager` и рабочая копия `C:\Users\Ochenstarik\projects\server-monitor-manager-task3-b3` заняты другими исполнителями; вторая содержит 24 незакоммиченных файла, которые нельзя потерять.

Задание самодостаточно: истории переписки у исполнителя нет, всё нужное — ниже.

## Зачем

Server Monitor Manager устанавливает на серверы бинарники, работающие с правами root, и управляет firewall. При этом:

- все GitHub Actions подключены по плавающим тегам, то есть содержимое шага сборки может измениться без нашего ведома и без изменения кода репозитория;
- у проекта нет `SECURITY.md` — для инструмента безопасности отсутствие канала сообщений об уязвимостях само по себе дефект;
- нет истории изменений, хотя опубликовано пять предрелизов;
- нет автоматического отслеживания уязвимых зависимостей.

Задача закрывает это. Она не касается кода, тестов и логики — только цепочки поставки и оформления репозитория.

## Работы

### 1. Запинить GitHub Actions по commit SHA

Полная инвентаризация на базе `b11c277` — 23 подключения, 5 различных действий, 5 файлов в `.github/workflows/`:

| Действие | Текущий тег | Вхождений |
|---|---|---|
| `actions/checkout` | `@v6` | 8 |
| `actions/setup-dotnet` | `@v5` | 5 |
| `actions/upload-artifact` | `@v6` | 5 |
| `actions/download-artifact` | `@v8` | 2 |
| `softprops/action-gh-release` | `@v2` | 3 |

Файлы: `linux-control-agent.yml`, `linux-platform-matrix.yml`, `linux-release.yml`, `windows-build.yml`, `windows-release.yml`.

Формат замены — полный 40-символьный SHA коммита, тег сохраняется комментарием:

```yaml
uses: actions/checkout@<40-символьный-sha>  # v6.x.x
```

**Требование, нарушение которого само по себе проваливает задание: SHA нельзя придумывать.** Каждый SHA обязан быть получен из upstream-репозитория действия и соответствовать указанному тегу. В отчёте привести способ получения — например вывод `gh api repos/actions/checkout/git/ref/tags/v6` или `git ls-remote --tags https://github.com/actions/checkout` — для каждого из пяти действий. Если тег указывает на аннотированный объект, разыменовать до commit SHA.

### 2. Автоматическое отслеживание зависимостей

`.github/dependabot.yml` с двумя экосистемами:

- `github-actions`, каталог `/`, еженедельно — чтобы запиненные SHA не превратились в вечно устаревшие;
- `nuget`, каталог `/`, еженедельно.

Ограничить число одновременно открытых PR разумным значением, чтобы бот не завалил доску.

### 3. Сузить права workflow

`linux-release.yml` и `windows-release.yml` объявляют `permissions: contents: write` на уровне всего workflow. Право записи нужно только шагу публикации релиза.

Перенести `permissions` на уровень job'а публикации; на уровне workflow оставить `contents: read`. Проверить остальные три workflow: если права шире необходимого — сузить, если уже минимальны — оставить и отметить это в отчёте.

### 4. `SECURITY.md` в корне

Содержательный документ, не заглушка:

- канал сообщения об уязвимости — приватный, не публичные issue;
- ожидаемое время первичного ответа;
- какие версии поддерживаются: сейчас alpha, поддерживается последний предрелиз;
- что считается уязвимостью в этом проекте, с опорой на его модель угроз (`docs/security-model.md`): обход разделения ролей, получение root вне typed provisioning, утечка приватных ключей или enrollment-токенов, обход kill switch, подмена артефактов поставки;
- что уязвимостью не считается: известные и задокументированные ограничения alpha — в частности отсутствие подписи release manifest и отсутствие доверенной подписи Windows MSIX, оба пункта открыты в `docs/roadmap.md`.

### 5. `CHANGELOG.md`

Формат Keep a Changelog, версии по существующим тегам. Содержание восстанавливать **из git-истории и текстов релизов**, не сочинять.

Опубликованные предрелизы: `v0.1.0-alpha.1`, `.2`, `.3`, `.4` (все 16.07.2026), `.5` (17.07.2026).

После `v0.1.0-alpha.5` в `main` вошли PR #6–#12 — они попадают в раздел `Unreleased`.

### 6. Оформление вклада

- `CONTRIBUTING.md`: как собрать, как прогнать тесты, требование прогонять Control suite **на Linux** (часть тестов помечена `OperatingSystem.IsLinux()` и на Windows молча пропускается), один PR — одна тема;
- `CODEOWNERS`;
- шаблоны issue (баг, задача) и pull request. В шаблоне PR — обязательный раздел с раздельным перечислением: что проверено локально, что в CI, что не проверялось и почему.

### 7. SBOM

Генерация SBOM (`dotnet CycloneDX`) и приложение его к артефактам релиза в `linux-release.yml` и `windows-release.yml`.

### 8. Удалить слитые ветки

На origin остались слитые ветки: `agent/remove-lightweight-server-references`, `codex/ttl-backup-acceptance`. Удалить.

**Не трогать** `hermes/release-alpha.6`, `hermes/sprint1-desktop-security`, `hermes/sprint1-server-security`, `hermes/task2-b-link-reconciliation` — их статус не проверен. `docs/product-horizons-and-integration` — открытый PR #13, не трогать.

## Границы

Задание не касается кода. Категорически не изменять:

- `src/ServerMonitorManager.Control/LinkService.cs`, `LinkPolicyApplier.cs`, `LinkReconciliationBackgroundService.cs`, `ControlStore.cs`, `ControlMaintenance.cs`, `ControlOptions.cs`, `appsettings.json`;
- `src/ServerMonitorManager.Provisioning.Helper/**`, `deploy/ochenstarik-smm-policy-apply`, `deploy/ochenstarik-server-monitor-manager.sh`;
- `src/ServerMonitorManager.Control/CertificateAuthority.cs`, `CertificateLifecycleService.cs`, `src/ServerMonitorManager.Agent/AgentClient.cs`;
- `src/ServerMonitorManager.Desktop/**`, `tests/**`.

Первые две группы заняты заданием B-3R, третья — заданием по жизненному циклу сертификатов, обе ведутся параллельно.

Также не входит в задание:

- `Directory.Build.props`, `Directory.Packages.props`, lock-файлы и `TreatWarningsAsErrors` — отдельная задача; включение сейчас сломает незавершённую ветку;
- изменение логики workflow: шаги, матрицы, условия остаются как есть, меняются только версии действий, права и добавление SBOM;
- merge собственного PR.

## Критерий приёмки

- ни одно подключение действия в пяти workflow не ссылается на плавающий тег; все 23 вхождения используют 40-символьный SHA с комментарием-тегом;
- в отчёте для каждого из пяти действий показано, откуда взят SHA;
- `dependabot.yml` покрывает `github-actions` и `nuget`;
- `contents: write` присутствует только на job'ах публикации релиза;
- `SECURITY.md` содержит канал, сроки, поддерживаемые версии и перечень того, что уязвимостью не считается, со ссылкой на открытые пункты roadmap;
- `CHANGELOG.md` соответствует реальным тегам и реальной истории; выдуманных записей нет;
- шаблон PR требует раздельного перечисления локальных проверок, CI и непроверенного;
- слитые ветки удалены, чужие не тронуты;
- ни один файл из списка границ не изменён — проверяется `git diff --name-only main`;
- CI зелёный на PR;
- PR создан, но не смержен.

## Отчётность

В описании PR раздельно перечислить:

- что запущено локально и с каким результатом;
- что запущено в CI, со ссылками на прогоны;
- что не запускалось и почему.

Физический acceptance (`SMM_ACCEPT_RESTORE=1 SMM_ACCEPT_REBOOT=1 tests/acceptance/three-server-mesh.sh`) выполнить нельзя — не выданы SSH- и topology-параметры. Contract- и mock-тесты за него не выдавать.

Отсутствие проверки — не дефект отчёта. Выдача непроверенного за проверенное — дефект, и он обесценивает весь отчёт.
