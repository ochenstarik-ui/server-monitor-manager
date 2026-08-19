# План доделывания Server Monitor Manager

Для исполнителя (Codex). Составлен 2026-08-04 против `main` @ `b11c277` плюс незакоммиченная ветка `hermes/task3-b3-fact-reconciliation`.

Нормативные документы в репозитории после коммита `4de849e`: [`docs/product-horizons.md`](../server-monitor-manager/docs/product-horizons.md), [`docs/approval-policies.md`](../server-monitor-manager/docs/approval-policies.md), [`docs/integration-kagent.md`](../server-monitor-manager/docs/integration-kagent.md), обновлённые `docs/roadmap.md` и `docs/security-model.md`.

---

## 0. Правила работы

Проверены на четырёх блоках, менять не нужно.

1. **Один PR — одна тема.** Смешанные PR не проходят review.
2. **Независимому review передаётся полный текст задачи**, а не только диф. Три из четырёх блоков дали находки класса «пропущенное требование», которые при diff-only ревью не ищутся вовсе.
3. **`SHA256SUMS` считается последним действием**, после всех правок отчётов, и включает все файлы пакета.
4. **Статус после merge — `merged / verified`**, а не «выполнено», если хотя бы один критерий сформулирован через фактическое поведение и не измерялся на реальной топологии.
5. **Гейты горизонтов обязательны.** Задача следующего горизонта не начинается, пока не закрыт предыдущий, даже если она кажется мелкой.
6. Формат пакета сдачи: `REPORT.md`, `TEST_EVIDENCE.md`, патч и/или ZIP, `CI_CHECKS.txt`, `INDEPENDENT_REVIEW.md`, `SHA256SUMS`.

Размеры: **S** — до дня, **M** — 2–4 дня, **L** — неделя и больше.

---

## Горизонт 0 — закрыть начатое

Ничто из горизонтов 1–3 не начинается до полного закрытия этого раздела.

### T0.1 — завершить B-3R · M · в работе

Ветка `hermes/task3-b3-fact-reconciliation`. Контракт: `B3R_SPEC.md`, уже принятый исполнителем.

Осталось по состоянию на 04.08: пакетная финальная верификация (`1 + k` вызовов `link-list` вместо двух), класс `Deferred` с инвариантом `Converged + Failed + Deferred == Examined`, R4 (source-generated DTO для orphan audit), R5 (Linux integration test на fact-first протокол), R6 (убрать fail-open default в `ILinkPolicyApplier`), R7 (форматирование вложенных scope).

Дополнительно, найдено при проверке: любое не-`active` значение статуса в `nodes.tsv` сейчас даёт exit 80 и уходит в `Deferred` навсегда. Мусор в поле статуса должен давать 78 — это повреждённое состояние, а не ожидаемое.

**Готово, когда:** PR создан, Linux CI зелёный впервые для этой ветки, merge выполнен.

### T0.2 — физический acceptance · S · внешний блокер

```bash
SMM_ACCEPT_RESTORE=1 SMM_ACCEPT_REBOOT=1 tests/acceptance/three-server-mesh.sh
```

Требуются `HUB_SSH_HOST`, `HUB_SSH_USER`, `SOURCE_SSH_HOST`, `SOURCE_SSH_USER`, `HOME_WG_IP`, `SECOND_WG_IP`, `SSH_IDENTITY_FILE`. Harness готов и содержит шаги firewall-restore без перезапуска Node и инъекции orphan-правила.

Исполнитель закрыть не может — нужна выдача доступов. Висит четвёртый блок подряд и блокирует критерий B6, Этап 3 и Этап 5 roadmap.

**Готово, когда:** прогон завершился `THREE_SERVER_ACCEPTANCE=PASS`, вывод приложен к пакету.

### T0.3 — роль Monitor в bootstrap · M

Сегодня SSH-мониторинг — функция, ради которой существует Desktop, — не имеет серверной части. В `deploy/ochenstarik-server-monitor-manager.sh` нет действия `install-monitor`, forced-command скрипт не поставляется, а парсер `SshMonitorService.QueryAsync` ожидает ключи (`PROTOCOL`, `CPU_COUNT`, `MEM_AVAILABLE_KB`, `DISK_INODES_TOTAL`…), которые в репозитории никто не производит.

Объём:

- `install-monitor PUBLIC_KEY` и `uninstall-monitor`;
- системный пользователь `ochenstarik-monitor`: nologin, без пароля, без sudo;
- `/usr/local/libexec/ochenstarik-smm-metrics`, root-owned `0755`, выдающий ровно снимок из `docs/installer-contract.md` §7 и read-only `mesh status`;
- `authorized_keys` с `command="…",restrict,no-pty,no-agent-forwarding,no-port-forwarding,no-X11-forwarding`;
- идемпотентность повторного запуска;
- контрактный тест формата снимка, общий для скрипта и парсера Desktop, чтобы они не разъезжались.

**Готово, когда:** чистый сервер попадает под мониторинг одной командой, README-инструкция соответствует коду, тест формата в CI.

### T0.4 — подписанная поставка · M

Сейчас bootstrap сверяет `ARCHIVE.sha256`, лежащий рядом в том же релизе, а manifest не подписан и содержит только хэш самого bootstrap. Кто может опубликовать релиз — ставит произвольный root-код на весь парк через `update-agent`.

Объём:

- manifest v2: хэши всех артефактов, версии Control/Agent/helper/Desktop, минимальные совместимые версии, `helper_protocol`;
- подпись `cosign sign-blob` keyless либо `minisign`; публичный ключ или issuer **вшит константой** в bootstrap и Desktop, не скачивается вместе с релизом;
- `verify-manifest` как отдельное действие; `verify_archive` сверяет хэш с manifest, а не с соседним файлом; старый путь только под явным `SMM_ALLOW_UNSIGNED=1` с предупреждением;
- отказ `update-control`/`update-agent` при несовместимой паре версий;
- `PROGRAM_VERSION` подставляется из тега при упаковке вместо `0.2.0-dev`;
- негативные тесты в CI: изменённый байт архива, подменённый хэш в manifest, manifest без подписи — все три отвергаются.

**Готово, когда:** установка и обновление возможны только из подписанного релиза, три негативных теста зелёные.

### T0.5 — жизненный цикл сертификатов · S

`CertificateAuthority.cs:55` выдаёт клиентские сертификаты на год, автопродления нет. Через год весь парк отваливается одновременно.

- сократить срок до 30–90 дней;
- автопродление Agent за треть срока до истечения, через существующий mTLS-канал;
- событие `certificate.expiring` в поток событий;
- документированная процедура ротации Control CA и повторной регистрации парка.

### T0.6 — гигиена репозитория и цепочки поставки · S

- пиннинг всех GitHub Actions по commit SHA (сейчас плавающие `@v6`, `@v5`, `@v2`);
- `.github/dependabot.yml` для `github-actions` и `nuget`;
- сужение `permissions: contents: write` до job'а публикации;
- `SECURITY.md` — обязателен для инструмента безопасности; `CHANGELOG.md`, `CONTRIBUTING.md`, `CODEOWNERS`, шаблоны issue/PR;
- SBOM (`dotnet CycloneDX`) в артефакты релиза;
- удалить слитые ветки `agent/remove-lightweight-server-references`, `codex/ttl-backup-acceptance`.

### T0.7 — базовая инженерная оснастка · S

Делается один раз и удешевляет всё последующее.

- `Directory.Build.props`: `Nullable`, `TreatWarningsAsErrors`, `EnableNETAnalyzers`, `AnalysisLevel latest-recommended`, детерминированная сборка;
- `Directory.Packages.props` (central package management) и `RestorePackagesWithLockFile` — сейчас сборки невоспроизводимы, что противоречит требованию закреплённой поставки;
- разделить тесты: `Core.Tests`, `Control.Tests`, `Agent.Tests`, `Helper.Tests`. Сейчас `LinuxMetricsTests`, `MetricBufferTests` и `ProvisioningHelperTests` лежат в проекте Control;
- тесты round-trip сериализации контрактов — при `PublishTrimmed=true` молчаливая потеря поля ничем не ловится.

**Гейт Горизонта 0.** Чистый сервер ставится из подписанного релиза одной командой, регистрируется одноразовым кодом, попадает под мониторинг без ручной правки `authorized_keys`, и после перезагрузки Hub и Node фактическое состояние Links совпадает с желаемым — измерено связностью на реальной топологии.

---

## Горизонт 1 — продукт для одного владельца

### T1.1 — каркас provisioning-модулей · M · делать первым

Сейчас вся логика мутации живёт в `TimezoneProvisioningExecutor` одним классом на 320+ строк, а `IsTimezoneOnly` жёстко фиксирует `VmSwappiness == 60`, пустые пакеты и `RebootPolicy = never`. Второй модуль скопирует backup/verify/rollback целиком.

```csharp
IProvisioningModule
    string ActionType { get; }
    string ModuleHash { get; }
    ValidationResult Validate(JsonElement parameters);
    Plan BuildPlan(JsonElement parameters);            // без мутаций
    ExecutionResult Execute(Plan plan, Grant grant);   // backup → mutate → verify → rollback
    FactualState Observe();
```

Перенести timezone на каркас без изменения поведения, покрыть тем же набором тестов. Добавление модуля после этого — один файл, одна схема, один тест.

**Готово, когда:** timezone работает через каркас, существующие тесты зелёные без правок логики.

### T1.2 — provisioning-модули по одному · L

Порядок по возрастанию риска: `system.locale` → `system.packages` (versioned allowlist уже есть) → `system.swap` → `user.create` → `user.disable` → `ssh.key.add` → `ssh.key.remove`.

Каждый модуль: versioned JSON schema, immutable plan, подтверждение по политике, factual verification, rollback, audit, тесты, прогон на VM-матрице. Новый пользователь по умолчанию без sudo. Приватные ключи на Hub не попадают.

### T1.3 — политики подтверждения · M

По `docs/approval-policies.md`. Девять режимов вместо бинарного подтверждения. Решение принимает Control, не клиент. Понижение режима для конкретного задания невозможно, изменение политики — аудируемое событие.

Зависимость: T1.1, потому что режим выбирается по `ActionType`.

### T1.4 — firewall и двухфазная миграция SSH · L · самая опасная задача проекта

По `docs/provisioning-vpn-requirements.md` §10. Порядок шагов там уже выписан и его надо соблюсти буквально.

Критическое требование: порт 22 невозможно закрыть до успешной второй SSH-сессии на новом порту. Отдельное подтверждение на закрытие старого порта. При любой ошибке — откат drop-in и правила firewall с сохранением активной сессии.

Обязательный тест: новый порт не поднялся → 22 остался открыт и доступен.

Делать только после T1.1–T1.3 и полного Горизонта 0.

### T1.5 — alert engine · M

Пороги, warning/critical, duration, cooldown, дедупликация, режим обслуживания, подтверждение, эскалация, история инцидентов, автозакрытие, тихие часы. Формат правила уже согласован:

```yaml
name: Disk almost full
metric: disk.used_percent
condition: "> 90"
duration: 10m
severity: critical
channels: [telegram, email]
cooldown: 30m
```

### T1.6 — Telegram, только чтение · S

`/status`, `/servers`, `/alerts`, `/links`, `/jobs`. Никаких мутаций, приватных ключей, управления CA и произвольных команд. Токен — секрет; до появления Vault хранить как остальные секреты Control, с маскированием в диагностике и аудите.

### T1.7 — наблюдение Docker и systemd · M

Сначала только чтение: контейнеры, образы, healthcheck, счётчики рестартов, отсутствующие лимиты, failed units, journald. Действия — отдельной задачей, только по allowlist, с подтверждением и аудитом, без произвольных аргументов.

### T1.8 — Backup Manager · M

Источники: каталоги, конфигурации, Docker volumes, Control DB и CA. Хранилища local и S3. Расписания, шифрование, retention, контрольные суммы, проверка архива, тестовое восстановление, оповещения об ошибке и об устаревшей копии.

### T1.9 — технический долг Control и Desktop · M · фоном

- `ControlStore.cs` 1576 строк: разделить на `ControlSchema`, `IdentityStore`, `MetricStore`, `LinkStore`, `AuditStore`, `IdempotencyStore`;
- `MainPage.xaml.cs` 1124 строки без MVVM и DI: вынести ViewModel, добавить unit-тесты Desktop;
- локализация: русские строки захардкожены в бизнес-логике (`SshMonitorService`, `ControlClientService`); ввести `.resw`, в исключениях — коды, текст на уровне UI;
- `HttpClient` создаётся на каждый запрос в `ControlClientService` и `AgentClient`: один `SocketsHttpHandler` с `PooledConnectionLifetime`;
- поток событий: durable log, `GET /events?since=<sequence>`, keepalive раз в 15 с, лимит подписок, экспоненциальный backoff с джиттером на клиенте;
- rate limiting не только на enrollment: политики на группы `agents` и `control`, `MaxConcurrentConnections`;
- единый источник валидаторов: `NodeIdValidator` и прочие живут в `Program.cs` и продублированы в bash и helper — три реализации одних правил.

**Гейт Горизонта 1.** Каждый мутирующий модуль имеет схему, immutable plan, подтверждение, factual verification, rollback и audit и прошёл физическую приёмку на VM-матрице.

---

## Горизонт 2 — платформа

### T2.1 — решение по публичному интерфейсу · S · до всякого кода

Заполнить раздел «Публичный интерфейс» в `docs/security-model.md`: отдельный listener и порт, отношение веб-сессий к mTLS-идентичностям, перечень операций, недоступных веб-сессии в принципе, привязка сессии к устройству, CSRF, rate limiting, повторная аутентификация.

Без этого решения Web UI не начинается.

### T2.2 — Secret Vault · M

Envelope encryption, версии, ротация, маскированный показ, scoped delivery, TTL, отзыв, redaction. Предусловие для Telegram-токена, ключей S3 и VPN-подписок.

### T2.3 — Web UI и аутентификация · L

Разделы: Dashboard, Servers, Metrics, Links, Provisioning, Alerts, Audit, Users, Automation identities, Backups, Settings. Аутентификация: пароль, TOTP, коды восстановления, WebAuthn, passkeys, доверенные устройства, сессии, RBAC, повторная аутентификация.

Операции класса firewall / CA / удаление Node / экспорт секретов — только по mTLS-идентичности, не по веб-сессии.

### T2.4 — решение по хранилищу логов · S · до кода

Логи journald + Docker + nftables + SSH со всех узлов не лягут на Control SQLite и раздуют резервные копии. Выбрать: отдельное хранилище, выгрузка вовне, либо только tail на узле без центрального retention.

### T2.5 — централизованные логи · L

После T2.4. Поиск, фильтры, уровни, retention, redaction, таймлайн инцидента, экспорт диагностики, корреляция.

### T2.6 — security posture · M

Оценка, объяснения, план исправления, безопасное применение, история, исключения, принятые риски, отчёты.

### T2.7 — multi-Hub, фаза 1 · M

Резервные копии БД и CA, шифрованное хранилище, проверка восстановления, ручное переключение. Репликация, active/passive и кворум — вне плана.

### T2.8 — один VPN-модуль · L

Xray VLESS Reality, полный жизненный цикл: preflight, установка, обновление, отключение, verification, rollback timer, routing exclusions, kill switch с management exceptions, аварийное отключение без Control, reboot-тест, reconciliation, ротация секретов.

Второй VPN-модуль не начинается, пока первый не прошёл физическую приёмку. Шесть наполовину сделанных VPN — это шесть способов потерять доступ к серверу.

---

## Горизонт 3 — интеграция с KAgent

По `docs/integration-kagent.md`. Строго в порядке: T3.1 → T3.4.

### T3.1 — обнаружение и идентичность · M

Unix-сокет `/run/server-monitor-manager/integration.sock`, `root:smm-integrations`, `0640`, каталог `0750`. Обязательно переиспользовать меры из `ProvisioningHelperServer`: `SO_PEERCRED` со сверкой uid, обработка вне цикла accept, ограничение параллелизма, таймаут соединения, буферное чтение, лимиты частоты. Роль `KAgentIntegration` с одноразовым token, сроком, областью Nodes и отзывом.

### T3.2 — чтение · M

Nodes, метрики, здоровье, события. Согласование версии протокола и фактически выданных возможностей. Невыдаваемые возможности отсутствуют как код, а не выключены настройкой.

### T3.3 — планы и запросы · L

`*.plan` и `*.request` порождают обычный typed provisioning job с immutable plan, режимом подтверждения, execution grant, factual verification и audit. Отдельного пути для AI нет. `*.request` возвращает идентификатор задания, а не результат.

### T3.4 — жизненный цикл Worker · L · наивысший риск в проекте

Инвариант недоверенного исполнителя из `docs/security-model.md` — главное требование, всё остальное вторично. Отдельный пользователь без sudo, контейнер с read-only rootfs, seccomp, AppArmor, сброшенными capabilities; нет доступа к сокету helper, к `agent.pfx`, `control-ca.crt`, `nodes.tsv`, каталогу rollback; нет сетевого доступа к Control API; порт наружу не публикуется.

Задачные Links: обязательный TTL, запрет destination = Hub или другой Worker, снятие по завершении, отказу, таймауту, offline, истечению аренды и аварийной остановке, с фактической проверкой снятия.

Аварийные средства: отзыв сертификата, остановка всех Worker, отключение задачных Links, блокировка новых запросов, режим только чтения, карантин, отключение сети, сохранение журналов.

---

## Что не делать

- Не начинать VPN-модули до закрытия Горизонта 1. Kill switch при незакрытом расхождении desired/factual — гарантированная потеря доступа к серверу.
- Не добавлять второй provisioning-модуль копированием timezone-исполнителя. Сначала T1.1.
- Не проектировать API KAgent против сущностей, которых нет: `containers`, `services`, `logs`, `backups` появляются вместе с самими подсистемами.
- Не начинать macOS/Linux Desktop и мобильные клиенты до стабилизации контрактов `Core` — они меняются почти каждым PR.
- Не делать репликацию и кворум multi-Hub.
- Не расширять расширенный мониторинг (SMART, RAID, GPU, UPS) — каждый пункт требует привилегированного сборщика и отдельной модели угроз.

---

## Критический путь

```
T0.1 ─┐
T0.3 ─┼─► T0.2 (внешний блокер) ─► гейт Г0 ─► T1.1 ─► T1.2 ─► T1.3 ─► T1.4 ─► гейт Г1 ─► Горизонт 2 ─► Горизонт 3
T0.4 ─┤
T0.5 ─┘
```

T0.6 и T0.7 идут параллельно и ничего не блокируют. T1.5–T1.9 параллельны T1.2–T1.4.

Единственное, что нельзя обойти работой исполнителя, — **T0.2**. Он дешевле любой другой задачи в этом плане и блокирует всё.
