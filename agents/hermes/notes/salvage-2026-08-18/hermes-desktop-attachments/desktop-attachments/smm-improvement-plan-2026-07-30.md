# Server Monitor Manager — полная инструкция по доработке

Ревизия: `main` @ `2d28b8d` («Execute confirmed timezone provisioning safely», 30.07.2026).
Публичный релиз: `v0.1.0-alpha.5` (17.07.2026) — отстаёт от `main` на 3 коммита (bootstrap + provisioning execution ещё не выпущены).
Открытых issue и PR нет, вся работа идёт напрямую в `main` через merged PR.

---

## 0. Как читать документ

| Приоритет | Значение |
|---|---|
| **P0** | Блокирует alpha-тест на реальных серверах: утечка секрета, потеря доступа, ложное состояние безопасности. Чинить до следующего тега. |
| **P1** | Блокирует beta: расхождение с собственным ТЗ, DoS, невоспроизводимая поставка. |
| **P2** | Долг качества: архитектура, тесты, DX, локализация. |

Каждый пункт: *что не так → где → что сделать → как проверить*.

---

## 1. Что фактически готово

Проект существенно более зрелый, чем типичный alpha: mTLS control plane с разделением ролей, идемпотентность, аудит, versioned SQLite (user_version до 8), bounded offline-буфер агента, подписанные ECDSA execution grants, restricted root helper через Unix-сокет, CI на Ubuntu VM + Debian systemd-контейнерах, 100-node нагрузочный тест.

**Реально работает:**
- Control API: enroll (node/device/automation), heartbeat, links CRUD + TTL, event stream (NDJSON), provisioning jobs, preflight facts / desired / drift, backup-create/restore CLI.
- Bootstrap: install/update/rollback/uninstall Control и Agent, mesh-init, node-code (`SMMNODE1/2`), peer-add (`SMMPEER1`), emergency-команда.
- Provisioning: `preflight` (read-only) и `system.base-install` — но **исполняется только смена timezone**.
- Desktop: 4 страницы, SSH-мониторинг, история метрик, графики, redacted diagnostics.

**Заявлено, но не реализовано (расхождение README/docs ↔ код):**

| Заявление | Реальность |
|---|---|
| `docs/installer-contract.md` §2 «роль Monitor»: пользователь `ochenstarik-monitor`, root-owned forced command | В `deploy/ochenstarik-server-monitor-manager.sh` **нет действия `install-monitor`**, нет самого forced-command скрипта. SSH-мониторинг из Desktop настраивается вручную и нигде не описан. |
| `docs/linux-bootstrap.md:82` «После перезапуска firewall разрешающие правила должны быть повторно применены Control» | В Control **нет кода переприменения** Active-Link'ов. |
| ТЗ §14: endpoints `/api/v1/nodes/{id}/capabilities`, `/configuration`, `/users`, `/vpn-profiles` | Отсутствуют. |
| ТЗ §7.1 «подписанный version manifest» | Manifest не подписан, содержит только hash bootstrap-скрипта, без версий Control/Agent/helper. |
| README: «`system.base-install`… factual verification» | Verification только для timezone; `IsTimezoneOnly()` отвергает всё остальное. |

---

## 2. P0 — критические

### P0-1. Enrollment-токен утекает через argv в `/proc/*/cmdline`

**Где:** `deploy/ochenstarik-server-monitor-manager.sh:564-570`

```bash
runuser -u "$AGENT_USER" -- env \
    ... \
    "SMM_EnrollToken=$ENROLL_TOKEN" \
    "$LIB_DIR/agent/ochenstarik-smm-agent"
```

Аргументы `env` — это командная строка процесса. `/proc/<pid>/cmdline` читается **любым локальным пользователем** (в отличие от `environ`, который защищён). Токен живёт 10 минут и даёт право получить сертификат Agent для этого Node.

**Что сделать:**
1. Ввести в `AgentOptions` поддержку `SMM_EnrollTokenFile` (путь к файлу, режим `0600`, владелец — agent).
2. В bootstrap: `install -m 0600 -o "$AGENT_USER" -g "$AGENT_USER" /dev/null "$token_file"`, записать токен через `printf`, передать путь, `shred -u` после enrollment.
3. Альтернатива без нового API: `runuser -u agent -- env SMM_EnrollTokenFile=/proc/self/fd/3 ... 3< <(printf '%s' "$ENROLL_TOKEN")`.
4. `AgentClient.EnrollAsync` — читать файл, `CryptographicOperations.ZeroMemory` после использования, `File.Delete`.

**Проверка:** тест в `tests/bootstrap/` — во время enrollment фоновый цикл читает `/proc/*/cmdline` и грепает подстроку токена; тест падает при совпадении.

---

### P0-2. После перезагрузки Hub все активные Links молча перестают работать

**Где:** `deploy/ochenstarik-smm-firewall.service:9-10`, `deploy/ochenstarik-server-monitor-manager.sh:263-281` (`write_mesh_firewall`), `src/ServerMonitorManager.Control/Program.cs` (нет startup-реконсиляции).

Юнит firewall при каждом старте делает `nft delete table inet ochenstarik_smm`, затем загружает `mesh.nft` с пустой цепочкой `links`. Все accept-правила, добавленные `link-connect`, исчезают. Control при старте **не переприменяет** Active-политики — реконсиляция (`LinkService.ReconcileDisabledLinksForNodeAsync`) переприменяет только **disabled**.

Итог: в БД и в Desktop Link показан `Active`, фактически трафик заблокирован. Это прямое нарушение критерия приёмки №12 собственного ТЗ («после reboot factual state соответствует desired»). Направление отказа безопасное (fail-closed), но состояние в UI ложное — а ложное «зелёное» состояние в security-инструменте хуже, чем красное.

**Что сделать:**
1. Добавить в policy-helper действие `link-list`, возвращающее фактические правила (парсинг `nft -j list chain`) как строки `source:target:proto:port`.
2. Новый `LinkStartupReconciliationService : IHostedService` в Control:
   - при старте и далее раз в `LinkExpirationPollSeconds × N` получать факт-список,
   - для каждой Link с `DesiredState=Active`, отсутствующей в факте → `ApplyConnectAsync`, событие `link.reapplied`,
   - для каждого факт-правила без записи в БД → `ApplyDisconnectAsync`, событие `link.orphan-removed` + audit,
   - публиковать `ActualState=Partial` до успешного восстановления.
3. Добавить `ActualState` колонку в UI Links и явный бейдж «desired ≠ factual».
4. `ochenstarik-smm-emergency firewall-restore` — после восстановления deny-by-default дёргать `systemctl reload ochenstarik-smm-control` либо сбрасывать маркер, чтобы Control переприменил.

**Проверка:** интеграционный тест с фейковым `ILinkPolicyApplier` (стартовое факт-состояние пустое → ожидаем N вызовов connect); + в `tests/acceptance/three-server-mesh.sh` при `SMM_ACCEPT_REBOOT=1` после reboot Hub проверять `nc -z` через Link, а не только состояние в API.

---

### P0-3. Приватный SSH-ключ пишется на диск в открытом виде каждые 30 секунд

**Где:** `src/ServerMonitorManager.Desktop/SshMonitorService.cs:187-210` (`MaterializePrivateKeyAsync`), вызывается из `RunRestrictedCommandAsync:122` на каждый опрос метрик.

Ключ хранится под DPAPI, но для запуска `ssh.exe` расшифровывается и пишется в `ApplicationData.TemporaryFolder` без ACL-ограничения, удаляется в `finally`. При падении процесса, kill'е или гонке файл остаётся. Частота: каждые 30 с × число серверов.

**Что сделать (в порядке предпочтения):**
1. **Лучшее:** отказаться от внешнего `ssh.exe` для мониторинга — перейти на in-process SSH-клиент (`SSH.NET`), ключ живёт только в памяти. Интерактивный терминал оставить на `ssh.exe`/`wt.exe`.
2. **Минимум, если п.1 откладывается:**
   - создавать файл один раз за сессию приложения, а не на каждый запрос;
   - `FileOptions.DeleteOnClose` + удержание `FileStream` на время жизни сессии;
   - явный DACL только для текущего пользователя (`FileSecurity`, `SetAccessControl`);
   - при старте приложения — чистка orphan-файлов `server-monitor-manager-ed25519-*` в TemporaryFolder;
   - `ProcessExit`/`AppDomain.UnhandledException` хук на удаление.

**Проверка:** unit-тест на чистку orphan-файлов; ручной тест — kill процесса в момент опроса, перезапуск, проверка что TemporaryFolder пуст.

---

### P0-4. `StrictHostKeyChecking=accept-new` без подтверждения fingerprint

**Где:** `src/ServerMonitorManager.Desktop/SshMonitorService.cs:132`

Первое подключение к любому серверу принимает любой host key без показа пользователю. Это ровно тот MITM-вектор, который остальная часть проекта тщательно закрывает (Control CA fingerprint подтверждается вручную, mTLS с CustomRootTrust). ТЗ §10 п.2 и §13 требуют фиксации host key.

**Что сделать:**
1. В `ServerProfileData` добавить `HostKeyFingerprint` (SHA-256) и `HostKeyAlgorithm`.
2. Первое подключение: `ssh-keyscan` → показать fingerprint в диалоге → после подтверждения записать в профиль и в `known_hosts`.
3. Далее всегда `StrictHostKeyChecking=yes`.
4. При смене ключа — блокирующее предупреждение с явным действием «принять новый ключ» + запись в аудит Desktop.
5. Интерактивный терминал (`OpenInteractiveTerminal:159-163`) сейчас вообще не передаёт `UserKnownHostsFile`/`StrictHostKeyChecking` — использует системный `known_hosts`. Привести к тому же контракту.

---

### P0-5. Root helper вешается насмерть от одного молчащего клиента

**Где:** `src/ServerMonitorManager.Provisioning.Helper/ProvisioningHelperServer.cs:37-53` и `185-203`

```csharp
var connection = await listener.AcceptAsync(cancellationToken);
try { await HandleAsync(connection, cancellationToken); }   // последовательно!
```

`HandleAsync` обрабатывается **синхронно в цикле accept**, а `ReadRequestAsync` читает по одному байту **без таймаута**. Любой процесс с доступом к сокету (группа `ochenstarik-smm-agent`) открывает соединение, ничего не пишет — и root-helper перестаёт обслуживать кого-либо навсегда. Провиженинг встаёт, `Requires=` в юните Agent не помогает (процесс жив).

**Что сделать:**
1. Обрабатывать соединение в отдельной задаче с ограничением параллелизма (`SemaphoreSlim(4)`).
2. Таймаут на всё соединение: `CancellationTokenSource.CreateLinkedTokenSource(ct)` + `CancelAfter(TimeSpan.FromSeconds(30))`.
3. Читать буфером (`ArrayPool<byte>`), а не по байту — сейчас 16 КБ = 16384 syscall'а.
4. **Проверять SO_PEERCRED**: `socket.GetRawSocketOption(SOL_SOCKET, SO_PEERCRED, …)`, сверять `uid` с ожидаемым uid пользователя Agent (передавать через env юнита). Сейчас достаточно членства в группе.
5. Rate limit: не более N запросов в минуту с одного uid, лог отклонений.

**Проверка:** тест `ProvisioningHelperTests` — открыть 2 соединения, первое молчит, второе шлёт валидный `preflight` и должно получить ответ за < 2 с.

---

### P0-6. Поставка не подписана: компрометация GitHub-аккаунта = RCE на всех Node

**Где:** `.github/workflows/linux-release.yml:33-65`, `deploy/ochenstarik-server-monitor-manager.sh:128-146` (`verify_archive`)

Bootstrap проверяет `ARCHIVE.sha256`, лежащий **рядом с архивом в том же релизе**. Manifest (`server-monitor-manager-bootstrap-manifest.json`) не подписан и не содержит хэшей архивов Control/Agent/helper — только хэш самого bootstrap. Кто может залить релиз, может залить и hash. `update-agent`/`update-control` принимают любой такой архив.

Собственный контракт (`installer-contract.md` §1, ТЗ §7.1) требует signed compatibility manifest — не выполнено (в roadmap отмечено как открытое, но это P0 для любой публичной поставки).

**Что сделать:**
1. Подписывать manifest — `cosign sign-blob` (keyless/OIDC) либо `minisign`. Публичный ключ **вшить в bootstrap-скрипт константой** и в Desktop.
2. Manifest должен содержать: версию тега, sha256 каждого артефакта (bootstrap, оба tar.gz, MSIX), версии Control/Agent/helper/Desktop, поддерживаемые runtime, минимальную совместимую версию Control.
3. `verify_archive` → `verify_manifest_signature` → сверка хэша архива **с manifest**, а не с соседним файлом.
4. Отказ обновления при несовместимой паре версий Control↔Agent↔helper (`installer-contract.md` §6 это уже требует).
5. Запиннить все GitHub Actions по commit SHA (`actions/checkout@<sha>`), включить `dependabot` для actions.

---

## 3. P1 — важные

### P1-1. Роль Monitor не реализована в bootstrap
Флагманская функция (SSH-мониторинг из Desktop) не имеет серверной части в поставке: нет `install-monitor`, нет forced-command скрипта, нет создания `ochenstarik-monitor`, нет установки публичного ключа из Desktop.

**Сделать:** добавить в bootstrap действие `install-monitor PUBLIC_KEY`:
- создать `ochenstarik-monitor` (nologin, без пароля, без sudo);
- `/usr/local/libexec/ochenstarik-smm-metrics` — root-owned `0755`, выдающий ровно снимок из `installer-contract.md` §7 (`PROTOCOL=1`, `HOSTNAME=…`) и read-only `mesh status`;
- `authorized_keys` c `command="/usr/local/libexec/ochenstarik-smm-metrics",no-pty,no-agent-forwarding,no-port-forwarding,no-X11-forwarding,restrict`;
- идемпотентность при повторном запуске, отдельный `uninstall-monitor`.
- Формат снимка вынести в общий тест-фикстуру, чтобы `SshMonitorService` и скрипт не разъезжались (сейчас парсер в `SshMonitorService.QueryAsync:72-106` знает ключи, которых никакой скрипт в репозитории не производит).

### P1-2. Приватный ключ Control CA доступен процессу Control
`create_control_certificates` (`:451`) экспортирует CA в PKCS#12 **с пустым паролем**, `0640 root:ochenstarik-smm-control`. Любой RCE в Control = выпуск произвольных Agent/Operator/Automation сертификатов + подпись execution grant для любого Node.

**Сделать:**
- разделить ключи: CA-ключ для выпуска сертификатов и **отдельный** ключ подписи execution grant (helper пиннит второй);
- защитить PFX паролем, подавать через `systemd LoadCredential=` (`ControlOptions.CertificateAuthorityPassword` уже есть — использовать);
- задокументировать процедуру ротации CA и re-enrollment всего флота;
- сократить срок сертификатов клиентов с 1 года (`CertificateAuthority.cs:55`) до 30–90 дней + автопродление Agent'ом за 1/3 срока до истечения.

### P1-3. Event stream без keepalive, без resume, без лимита подписчиков
`Program.cs:922-937` + `ControlEventBroker.cs`. Канал `DropOldest(256)` — при медленном клиенте события теряются молча. Реконнект Desktop (`ControlClientService.ListenAsync:181-184`, фиксированные 5 с без backoff и джиттера) начинает с текущего момента: всё, что произошло во время разрыва, потеряно, а полного refresh на реконнекте нет.

**Сделать:** таблица `control_events` с retention; `GET /api/v1/control/events?since=<sequence>`; heartbeat-кадр раз в 15 с (`{"type":"ping"}`) для детекта мёртвого TCP; лимит подписок на identity; экспоненциальный backoff с джиттером на клиенте; после реконнекта — принудительный refresh inventory и links.

### P1-4. Rate limiting только на enrollment
`Program.cs:19-31` — политика `"enrollment"`. Heartbeat, links, provisioning, events — без лимитов. Скомпрометированный Agent-сертификат = неограниченная запись метрик и рост БД (retention сработает поздно).

**Сделать:** политики на группу `agents` (по `NameIdentifier`), на `control` (мутации), и лимит одновременных event-подписок. Плюс `Kestrel.Limits.MaxConcurrentConnections`.

### P1-5. Провиженинг реализован на ~5% от ТЗ
`system.base-install` исполняет только timezone; `IsTimezoneOnly` (`TimezoneProvisioningExecutor.cs:229-238`) жёстко требует `VmSwappiness == 60`, пустые пакеты, `RebootPolicy == "never"`. Этапы 9–12 ТЗ (базовая настройка, пользователи, firewall/SSH-миграция, Xray) — нули.

**Сделать: вынести общий каркас исполнителя,** прежде чем писать второй модуль. Сейчас timezone-логика (проверка grant → consumption record → backup → mutate → verify → rollback) вшита в один класс на 320 строк; второй модуль скопирует её целиком.

```
IProvisioningModule
  string ActionType { get; }
  string ModuleHash { get; }
  ValidationResult Validate(JsonElement parameters);
  Plan BuildPlan(parameters);           // без мутаций
  ExecutionResult Execute(plan, grant); // backup → mutate → verify → rollback
  FactualState Observe();
```

Порядок модулей (по риску, от низкого к высокому): `system.timezone` (готов) → `system.locale` → `system.packages` (versioned allowlist уже есть) → `system.swap` → `users.*` → `firewall.*` → `ssh.migrate` (двухфазный) → `xray.*`.

### P1-6. Grant consumption-записи копятся без ограничения
`TimezoneProvisioningExecutor.cs:79-95` пишет `grant-<sha>.consumed.json` в rollback-каталог навсегда. За год активного провиженинга — десятки тысяч файлов в одном каталоге, замедление `CreateNew`.

**Сделать:** чистка записей старше 24 ч (grant живёт 2 мин) при каждом старте helper'а; тест на идемпотентность повторного grant после чистки (должен по-прежнему отклоняться в пределах TTL — хранить не файл, а компактный журнал nonce с TTL).

### P1-7. HttpClient создаётся на каждый запрос
`ControlClientService.cs:254-281` и `AgentClient.CreateHttpClient:390-416` — новый `HttpClientHandler` + TLS handshake на каждый вызов, исчерпание сокетов в TIME_WAIT при 30-секундном опросе.

**Сделать:** один `SocketsHttpHandler` на сессию (`PooledConnectionLifetime = 5 min`), переиспользование `HttpClient`. Сертификат сессии держать в `X509Certificate2` с явным `Dispose` при смене identity.

### P1-8. Атомарность установки нарушена
`install_tree_atomic:200-213`: `rm -rf destination; mv staging destination`. Между этими командами каталога с бинарями не существует — падение/kill/OOM оставляет систему без Control.

**Сделать:** `mv dest dest.old.$$` → `mv staging dest` → `rm -rf dest.old.$$`; при неудаче второго шага — вернуть `.old`.

### P1-9. `restore_backup` распаковывает tar в `/` без валидации
`:711` `tar -C / -xzf "$archive"` — в отличие от `verify_archive`, без проверки путей и без sha256. Каталог `0700 root`, риск ограничен, но rollback — именно тот путь, который выполняется в аварийной ситуации.

**Сделать:** записывать `.sha256` рядом с каждым backup'ом, проверять перед распаковкой; прогонять тот же allowlist путей.

### P1-10. Нет фильтрации входящего mesh-трафика на Node
Node получает `AllowedIPs = 10.77.0.0/24` (`:407`) и **не имеет никакой nftables-политики**. Вся изоляция держится на forward-цепочке Hub. Hub (или тот, кто его скомпрометировал) имеет неограниченный доступ ко всем портам всех Node поверх `smm0`.

**Сделать:** ставить на Node input-цепочку `iifname "smm0"` с deny-by-default и разрешением только на порты, фигурирующие в Link'ах, где этот Node — target. Разрешающие правила Node применяет сам Agent по данным `/api/v1/automation/links`-подобного эндпоинта (нужен `/api/v1/agents/links/inbound`). Это же закрывает единственную точку отказа изоляции.

### P1-11. IPv6 в mesh не определён
`mesh.nft` — `table inet`, но политика описана только для IPv4 (`ip saddr/daddr` в policy-helper). Если на Node включён IPv6 внутри `smm0` (не включён, но AllowedIPs не запрещает), правил нет. Явно задать: mesh — IPv4-only, IPv6 в `smm0` запрещён отдельным правилом drop.

---

## 4. P2 — качество, архитектура, процесс

### Код

1. **`ControlStore.cs` — 1576 строк, «божественный объект»**: схема + миграции + токены + enrollment + heartbeat + links + audit + idempotency + provisioning-делегаты. Разделить по агрегатам: `ControlSchema` (миграции), `IdentityStore`, `MetricStore`, `LinkStore`, `AuditStore`, `IdempotencyStore`; provisioning уже частично вынесен. Тесты уже намекают на границы (`ControlStoreTests` 809 строк).
2. **`MainPage.xaml.cs` — 1124 строки code-behind без MVVM и DI.** Вынести в `ViewModel` c `INotifyPropertyChanged`, сервисы — через `IServiceProvider` в `App.xaml.cs`. Сейчас логика Desktop покрыта одним PowerShell-скриптом на 39 строк (`tests/windows/Test-DesktopContracts.ps1`) — фактически не покрыта.
3. **Хардкод русских строк в бизнес-логике**: `SshMonitorService.cs:117,155,216,220,226`, `ControlClientService.cs:42,51,60,73,83,89,119,297,318`. При 12 переводах README само приложение не локализовано. Ввести `.resw` + `x:Uid`, коды ошибок вместо текста в исключениях (текст — на уровне UI).
4. **Валидаторы живут в `Program.cs`** (`NodeIdValidator`, `LinkPolicyValidator`, `ProvisioningJobValidator`, …, строки 1005-1136) и дублируются в bash (`validate_node_id`, `validate_port`) и в helper. Три независимые реализации одних правил. Вынести в `Core`, а для bash — генерировать regexp из одного источника либо покрыть контрактным тестом (`test-bootstrap-contract.sh` уже есть — расширить: каждый валидатор проверяется одинаковым набором кейсов на обеих сторонах).
5. **Нет `Directory.Build.props`**: не заданы `Nullable`, `TreatWarningsAsErrors`, `EnableNETAnalyzers`, `AnalysisLevel`, `InvariantGlobalization`, детерминированная сборка. Добавить.
6. **Нет `Directory.Packages.props`** (Central Package Management) и нет lock-файлов (`RestorePackagesWithLockFile`) — сборки невоспроизводимы, что противоречит требованию pinned-поставки.
7. `LinkPolicyApplier` вызывает `sudo` per-operation (`:48`); при массовом переприменении (P0-2) это N процессов. Добавить batch-режим helper'а: `link-apply -` со списком правил на stdin, одна транзакция `nft -f`.
8. `ProvisioningProcessRunner.Run` (`:329-343`) — `Task.WaitAll` на потоке; при чтении больших выводов возможен deadlock-паттерн. Переписать на async, добавить лимит размера stdout/stderr (сейчас без ограничения — `timedatectl` безопасен, следующие модули нет).

### Тесты

9. Всё в одном проекте `ServerMonitorManager.Control.Tests` — включая `LinuxMetricsTests` (Agent), `MetricBufferTests` (Agent), `ProvisioningHelperTests` (Helper). Разделить: `Core.Tests`, `Control.Tests`, `Agent.Tests`, `Helper.Tests`, `Desktop.Tests`.
10. Нет тестов на: сериализацию контрактов (source-generated JSON + trimming — риск молчаливой потери полей при `PublishTrimmed=true`), отзыв сертификата → отказ в доступе, миграции SQLite v1→v8 на реальном старом файле, поведение при повреждённой БД.
11. Нет измеримых performance budget'ов (в roadmap отмечено). Задать: p95 heartbeat < 50 мс при 100 Node, старт Control < 2 с, память Agent < 60 МБ, размер БД на Node/сутки.
12. `HubLoadTests` (126 строк) гоняет 100 Node в одном процессе — добавить soak на 24 ч в nightly workflow с проверкой отсутствия роста памяти и размера БД.

### Процесс и репозиторий

13. Отсутствуют: `CHANGELOG.md`, `SECURITY.md` (куда слать уязвимости — обязательно для security-инструмента), `CONTRIBUTING.md`, `CODEOWNERS`, шаблоны issue/PR, `.github/dependabot.yml`.
14. Actions не запиннены по SHA; в `linux-release.yml` `permissions: contents: write` на весь workflow — сузить до job'а публикации.
15. Нет SBOM (`dotnet CycloneDX`) и нет сканирования зависимостей — для проекта, который ставит бинарь с root-правами, это ожидаемый минимум.
16. 12 переводов README рассинхронизированы с релиз-статусом (пункт открыт в roadmap Этап 6). Сделать один источник — секцию статуса генерировать скриптом из тега, переводы проверять CI на наличие актуального номера версии.
17. Ветки `agent/remove-lightweight-server-references` и `codex/ttl-backup-acceptance` слиты, но не удалены — почистить.

---

## 5. План работ по спринтам

### Спринт 1 — «Безопасно тестировать на живых серверах» (P0)
- [ ] P0-1 токен через файл/fd + тест на `/proc/*/cmdline`
- [ ] P0-5 helper: параллелизм, таймауты, буферное чтение, SO_PEERCRED
- [ ] P0-3 SSH-ключ: чистка orphan, DACL, одноразовая материализация за сессию
- [ ] P0-4 host key fingerprint в профиле + `StrictHostKeyChecking=yes`
- **Критерий выхода:** `v0.1.0-alpha.6`, ни один секрет не наблюдаем локальным непривилегированным пользователем; helper выдерживает молчащего клиента.

### Спринт 2 — «Состояние в UI = состояние в системе» (P0-2, P1-3, P1-10)
- [ ] `link-list` в policy-helper, `LinkStartupReconciliationService`
- [ ] `ActualState`/drift-бейдж в Desktop
- [ ] durable event log + `?since=` + keepalive + backoff
- [ ] input-политика на Node
- **Критерий выхода:** сценарий из `three-server-acceptance.md` с `SMM_ACCEPT_REBOOT=1` проходит **с фактической проверкой связности** (`nc -z`) после reboot Hub и Node.

### Спринт 3 — «Поставка, которой можно доверять» (P0-6, P1-2, P1-8, P1-9)
- [ ] подписанный compatibility manifest + вшитый публичный ключ
- [ ] проверка совместимости версий при update
- [ ] разделение CA-ключа и grant-ключа, пароль через `LoadCredential`
- [ ] атомарная замена дерева, checksum для backup'ов
- [ ] пиннинг actions, dependabot, SBOM, `SECURITY.md`
- **Критерий выхода:** `v0.2.0-beta.1`; подмена архива в релизе отвергается bootstrap'ом; тест на это в CI.

### Спринт 4 — «Каркас провиженинга» (P1-5, P2-1, P2-4)
- [ ] `IProvisioningModule` + перенос timezone на него без изменения поведения
- [ ] versioned JSON schema per action, единый источник валидаторов в `Core`
- [ ] модули `system.locale`, `system.packages`, `system.swap` с backup/verify/rollback
- [ ] распил `ControlStore`
- **Критерий выхода:** Этап 9 roadmap закрыт наполовину; добавление нового модуля = один файл + схема + тест.

### Спринт 5 — «Роль Monitor и Desktop» (P1-1, P2-2, P2-3)
- [ ] `install-monitor` + forced-command скрипт + контрактный тест формата снимка
- [ ] MVVM + DI + unit-тесты Desktop
- [ ] локализация `.resw`, коды ошибок вместо текста
- **Критерий выхода:** чистый сервер настраивается под мониторинг одной командой; README-инструкция соответствует коду.

### Спринт 6 — «SSH-миграция и firewall» (Этап 10 ТЗ)
Двухфазная миграция порта — самая опасная операция во всём проекте (потеря доступа). Делать только после спринтов 1–4, обязательно с VM-матрицей и тестом «новый порт не поднялся → 22 остался открыт».

---

## 6. Чек-лист готовности к публичной beta

- [ ] Ни один секрет (токен, ключ, subscription URL) не наблюдаем в `argv`, логах, diagnostics, SQLite — покрыто автотестом
- [ ] Все артефакты релиза подписаны, bootstrap проверяет подпись
- [ ] Совместимость Control↔Agent↔helper↔Desktop проверяется перед update и перед выполнением job
- [ ] После reboot Hub и любого Node factual state == desired state (проверено связностью, не только API)
- [ ] Отзыв сертификата немедленно закрывает доступ — тест
- [ ] Каждая мутирующая операция: idempotency + audit + verification + rollback — тест на каждую
- [ ] Физическая приёмка `three-server-acceptance.md` с `SMM_ACCEPT_RESTORE=1 SMM_ACCEPT_REBOOT=1`
- [ ] Debian VM с настоящим reboot (сейчас только systemd-контейнеры)
- [ ] Доверенная подпись Windows MSIX
- [ ] Performance budget зафиксированы и проверяются в nightly
- [ ] `SECURITY.md`, `CHANGELOG.md`, SBOM
- [ ] Переводы README синхронизированы с релиз-статусом

---

## 7. Чего делать не стоит

- **Не начинать Xray (Этапы 11–12) до закрытия P0/P1.** Xray добавляет системный kill switch и policy routing — при текущем разрыве desired/factual (P0-2) это гарантированная потеря доступа к серверу.
- **Не расширять `system.base-install` копированием `TimezoneProvisioningExecutor`.** Сначала каркас (P1-5), иначе шесть копий логики backup/verify/rollback.
- **Не добавлять macOS/Linux/мобильные клиенты** (Этап 13) до стабилизации контрактов `Core` — сейчас они меняются каждым PR.
- **Не отключать `PublishTrimmed`** ради удобства, но и не доверять ему без тестов сериализации (P2-10): source-generated JSON контекст есть, но контрактных тестов на round-trip нет.
