# Техническое задание: Provisioning серверов и Xray VPN

## 1. Статус и граница проекта

Этот документ является частью технического задания **Server Monitor Manager**. Все описанные здесь исходники, bootstrap-компоненты, схемы, manifests, helper-модули, тесты и release artifacts принадлежат только репозиторию `ochenstarik-ui/server-monitor-manager`.

Проект не использует исходники, установщики, releases или runtime-компоненты других репозиториев. Совместное версионирование и межрепозиторные зависимости запрещены. Source of truth для каждой поддерживаемой операции находится в этом репозитории и публикуется в одном релизе с совместимыми Control, Agent и Desktop.

## 2. Назначение

Добавить управляемую первоначальную настройку Ubuntu/Debian из Windows-приложения. Пользователь один раз запускает собственный bootstrap Server Monitor Manager на новом сервере, привязывает Node к Control Hub одноразовым кодом, а последующие операции выполняет через типизированные задания с проверкой результата, журналом, аудитом и безопасным откатом.

Функциональность включает:

- первичную проверку совместимости сервера;
- базовую настройку ОС;
- безопасное управление firewall и миграцию SSH-порта;
- управление Unix-пользователями и публичными SSH-ключами;
- установку и обслуживание Xray;
- VPN для всего сервера или одного Unix-пользователя;
- reconciliation фактического и желаемого состояния после reconnect/reboot.

## 3. Цели безопасности

- не передавать произвольные root-команды через Control API;
- не хранить root/sudo-пароли, приватные Node-ключи и открытые VPN subscription URL на Hub;
- выполнять root-операции только через локальный helper с фиксированными action id и JSON Schema;
- не закрывать действующий SSH-доступ до подтверждения нового подключения;
- предварительно проверять опасную конфигурацию и создавать root-only backup;
- обеспечивать idempotency, аудит, verification и rollback каждой mutation;
- не позволять компрометации одного Node запускать задания на другом.

## 4. Не входит в первую версию

- системы без systemd и APT;
- CentOS, Fedora, Alpine, Arch Linux и производные;
- автоматическое изменение cloud firewall/security groups;
- выполнение произвольного Bash или terminal input через Provisioning API;
- хранение root/sudo-пароля в Desktop, Control или Agent;
- VPN для отдельных процессов одного пользователя;
- несколько одновременных VPN-профилей у одного пользователя;
- установка пакетов вне утверждённого versioned manifest;
- удалённый bootstrap с передачей sudo-пароля из Desktop;
- macOS/Linux Desktop и мобильные клиенты в рамках provisioning alpha.

## 5. Поддерживаемые платформы

- Ubuntu Server 22.04 и 24.04;
- последующая Ubuntu LTS только после включения в CI matrix;
- Debian 12 и 13;
- `amd64` и `arm64`;
- systemd, OpenSSH и APT;
- UFW с nftables backend либо root-owned nftables-таблицы проекта.

Preflight определяет ОС, версию, архитектуру, init system, текущий SSH-порт, host key, активный firewall, IPv4/IPv6, права bootstrap-пользователя, APT и совместимость Agent/helper.

## 6. Целевая архитектура

```text
Windows Desktop
    |
    | mTLS/HTTPS, Operator identity
    v
Control Hub + SQLite
    |
    | desired state, typed jobs, events
    v
Outbound Agent session
    |
    | local Unix socket, action id + strict JSON
    v
root-owned provisioning helper
    |
    +-- base-setup
    +-- firewall-apply
    +-- ssh-migrate
    +-- user-create/update/delete
    +-- vpn-install/apply/disable
    +-- verify
    +-- rollback
```

Control Hub хранит desired state, безопасные метаданные и аудит. Agent получает только задания собственного `node_id`. Helper повторно валидирует payload, использует фиксированный `PATH`, очищенный environment и никогда не принимает shell text или произвольный путь.

Monitoring Agent и Provisioning Agent могут поставляться одним бинарником, но используют разные API scopes, очереди, разрешения и журналы. Monitoring не получает root-доступ автоматически.

## 7. Собственный bootstrap

### 7.1. Поставка

Bootstrap Server Monitor Manager хранится в этом репозитории и прикладывается к release вместе с:

- SHA-256 checksum;
- подписанным version manifest;
- версиями совместимых Desktop, Control, Agent и helper;
- JSON schemas поддерживаемых действий;
- self-contained binaries для `linux-x64` и `linux-arm64`.

Production-установка использует только закреплённый tag/release, а не mutable `main`. Checksum и manifest проверяются до запуска. Обновление выполняется атомарно с root-only backup.

### 7.2. Регистрация

1. Пользователь скачивает bootstrap из release Server Monitor Manager.
2. Проверяет checksum и запускает его один раз через локальный `sudo`.
3. Bootstrap показывает fingerprint Control CA и запрашивает одноразовый enrollment-код.
4. На Node локально создаются приватный ключ и CSR.
5. После регистрации устанавливаются Agent, ограниченный helper и systemd units.
6. Enrollment закрывается после успешной регистрации.
7. Приватный Node key никогда не покидает сервер.

Sudo-пароль вводится только в локальном терминале сервера. Удалённый ввод пароля из Desktop не входит в первую версию.

## 8. ProvisioningJob

### 8.1. Состояния

```text
Queued -> Preflight -> AwaitingConfirmation -> Running -> Verifying -> Completed
                       |                       |           |
                       +-> Cancelled           +-> Failed  +-> NeedsReconciliation
                                                   |
                                                   +-> RollingBack -> RolledBack
                                                                    -> RollbackFailed
```

После потери связи результат не считается автоматически успешным или неуспешным. Состояние `NeedsReconciliation` требует сверки системы после reconnect.

### 8.2. Поля

- `job_id`, `node_id`, action type и schema version;
- обязательный `idempotency_key`;
- hash нормализованного запроса;
- версия и SHA-256 helper module;
- инициатор, audit reason и timestamps;
- безопасные параметры без секретов;
- текущий шаг, процент и длительность;
- структурированные redacted events;
- preflight и confirmation records;
- backup/rollback identifier;
- desired state и verification result;
- безопасный error code;
- TTL задания.

Повтор с тем же idempotency key и телом возвращает исходное задание. Повтор с другим телом отклоняется. На одном Node одновременно выполняется только одно несовместимое опасное задание.

После перезапуска Agent сначала проверяет фактическое состояние и только затем продолжает шаг либо выполняет rollback. Отзыв или повторная регистрация Node инвалидирует незавершённые опасные задания.

## 9. Базовая настройка

Desktop-мастер предоставляет:

- timezone;
- locale для новых сессий;
- `apt update` и опциональный `apt upgrade`;
- versioned package groups с раскрываемым точным списком;
- swap: выключен, автоматически рассчитан или задан явно;
- `vm.swappiness`;
- unattended upgrades;
- предварительный план;
- отдельное подтверждение перезагрузки.

Требования:

- операции идемпотентны;
- неизвестные package id отклоняются;
- APT lock отображается как ожидание;
- перед изменением locale, fstab, sysctl и APT создаётся root-only backup;
- существующий swap не заменяется без отдельного подтверждения;
- symlink в управляемом пути отклоняется;
- APT output проходит secret redaction;
- после выполнения проверяются timezone, locale, swap, packages и reboot requirement.

## 10. Firewall и безопасная миграция SSH

Пользователь задаёт IPv4/IPv6 policy, новый SSH-порт, правила port/protocol, необязательный source CIDR, описание и судьбу ранее управляемых правил. Никакие прикладные порты не открываются автоматически. UI отдельно предупреждает, что локальный firewall не изменяет cloud firewall провайдера.

Миграция SSH выполняется двухфазно:

1. Определить текущую сессию, effective port и host key.
2. Проверить диапазон и отсутствие конфликта через `ss -lnt`.
3. Открыть новый TCP-порт в managed firewall rules.
4. Создать отдельный managed `sshd_config.d` drop-in.
5. Выполнить `sshd -t` и проверить `sshd -T`.
6. Выполнить reload, но не stop SSH.
7. Desktop открывает второе тестовое соединение.
8. Проверяются host key, authentication и безопасная probe-команда.
9. При успехе Desktop обновляет профиль на новый порт.
10. Отдельным подтверждением пользователь закрывает порт 22.
11. Desktop повторно проверяет новый доступ и отсутствие публичного порта 22.

До успешного шага 8 запрещено удалять старое правило. При ошибке новый drop-in и firewall rule откатываются, активная SSH-сессия сохраняется.

## 11. Управление пользователями

Страница **Пользователи** поддерживает:

- список обычных и административных аккаунтов;
- отдельный фильтр системных аккаунтов;
- создание валидированного login и home;
- добавление/удаление SSH public key и показ fingerprint;
- password authentication policy;
- назначение/отзыв sudo;
- отдельное опасное подтверждение passwordless sudo;
- блокировку/разблокировку;
- активные процессы и systemd user services;
- завершение сессий перед удалением;
- удаление с сохранением либо удалением home;
- назначение одного VPN-профиля.

Новый пользователь по умолчанию не получает sudo. Приватные SSH-ключи не загружаются на Hub. Helper проверяет owner/group и режимы `0700/0600`.

Запрещено удалять `root`, пользователей Control/Agent/monitoring и текущего bootstrap-администратора, пока административный доступ не передан другому аккаунту.

## 12. Xray VPN

### 12.1. Профиль и секреты

VPN-профиль содержит название, источник HTTPS subscription или одну VLESS-ссылку, endpoint, DNS mode, IPv6 policy, kill switch, исключаемые CIDR, время проверки, внешний IP до/после и состояния Xray/routing.

Subscription URL является секретом. Desktop шифрует его на публичный encryption key конкретного Node. Control хранит ciphertext и минимальные маскированные metadata, но не может расшифровать URL. Node decrypt key не покидает Node. Диагностика и аудит показывают только scheme, host и masked path.

### 12.2. VPN для сервера

- весь исходящий трафик направляется через Xray;
- исключаются loopback, private/link-local, Mesh WireGuard, Control Hub, SSH/control traffic и VPN endpoint;
- конфигурация Xray проверяется до переключения маршрутов;
- запускается automatic rollback timer;
- verification проверяет внешний IP, TCP, UDP, DNS и IPv6 policy;
- при ошибке маршруты автоматически снимаются;
- kill switch блокирует прямой публичный выход, сохраняя management exceptions;
- профиль можно включать, отключать, проверять и обновлять без переустановки Xray.

### 12.3. VPN для Unix-пользователя

```text
process UID -> nftables meta skuid/cgroup -> fwmark -> policy route -> Xray TPROXY
```

- назначение хранится по UID с проверкой username;
- одному пользователю назначается не более одного активного профиля;
- root и служебные пользователи требуют отдельного опасного подтверждения;
- Xray и management traffic исключаются из перехвата;
- поддерживаются TCP, UDP и DNS;
- прямой IPv6 блокируется, если профиль не обеспечивает защищённый IPv6;
- kill switch блокирует пользователя при неактивном Xray/routing unit;
- остальные пользователи и службы сохраняют обычный маршрут;
- смена UID или удаление пользователя отключает assignment и создаёт audit warning;
- probes запускаются именно от назначенного UID.

## 13. Desktop UI

В карточке сервера добавляются разделы:

- **Подготовка** — bootstrap, capabilities и этапы;
- **Базовая настройка** — locale, timezone, packages и swap;
- **Firewall и SSH** — rules, migration и probes;
- **Пользователи** — accounts, keys, sudo и VPN assignment;
- **VPN** — profiles, modes, external IP и diagnostics;
- **Задания** — progress, logs, retry и rollback;
- **Конфигурация** — desired/factual state и drift.

Статус определяется probes, а не локальными галочками:

```text
Подключён -> Базово настроен -> Firewall применён -> Новый SSH проверен
          -> Администратор создан -> Agent активен -> VPN проверен
```

Закрытие текущего SSH-порта, удаление администратора, системный VPN и kill switch требуют отдельных подтверждений и не объединяются в одну mutation.

## 14. API

Минимальные Operator endpoints:

```text
GET    /api/v1/nodes/{nodeId}/capabilities
GET    /api/v1/nodes/{nodeId}/configuration
POST   /api/v1/nodes/{nodeId}/provisioning/preflight
POST   /api/v1/nodes/{nodeId}/provisioning/jobs
GET    /api/v1/provisioning/jobs/{jobId}
GET    /api/v1/provisioning/jobs/{jobId}/plan
POST   /api/v1/provisioning/jobs/{jobId}/confirm
POST   /api/v1/provisioning/jobs/{jobId}/cancel
POST   /api/v1/provisioning/jobs/{jobId}/rollback
GET    /api/v1/provisioning/jobs/{jobId}/events
GET    /api/v1/nodes/{nodeId}/users
GET    /api/v1/nodes/{nodeId}/vpn-profiles
```

Agent получает только задания своего Node. Automation identity не создаёт provisioning jobs, не читает VPN secrets и не управляет пользователями. Mutation требует Operator certificate, idempotency key и audit reason.

Каждый action type имеет отдельную versioned JSON Schema. Неизвестные смысловые поля отклоняются на Control, Agent и helper.

## 15. Хранение и аудит

Control SQLite хранит job metadata, безопасные параметры, состояния, errors, desired/factual snapshots, user metadata без password hashes, encrypted VPN profiles, assignments, confirmations и audit records.

Не хранятся sudo/root-пароли, приватные Node keys, plaintext subscription URL, `/etc/shadow` и terminal input.

Аудит содержит инициатора, Node, action, before/after, idempotency key, reason, module hash, verification и rollback result. Срок хранения журналов и событий ограничивается настройками retention.

## 16. Наблюдаемость и восстановление

UI показывает текущий шаг и длительность, APT lock, ожидание confirmation/reconnect, последние redacted stdout/stderr, error code, состояние старого SSH-доступа, rollback result, внешний IP, leak-test и configuration drift.

На каждом Node устанавливается локальная аварийная команда, работающая без Control Hub и способная:

- отключить managed VPN и kill switch;
- восстановить последний рабочий SSH drop-in;
- восстановить только принадлежащие проекту firewall rules;
- показать последние backup identifiers и verification results.

Аварийная команда не предоставляет удалённый root shell.

## 17. Тестирование

### Unit

- validation всех action schemas;
- idempotency и конфликт request body;
- state machine и TTL;
- secret redaction;
- package allowlist;
- username/UID, port, protocol, CIDR и timezone validation;
- role isolation;
- блокировка параллельных опасных заданий.

### Integration

- Control -> Agent -> helper boundary;
- рестарт Control/Agent в каждом состоянии;
- token expiration и certificate revocation;
- повторная доставка action;
- helper failure, rollback и rollback failure;
- неверный module checksum;
- Node-specific encryption и невозможность decrypt на Hub;
- reconciliation после reconnect.

### VM end-to-end

Матрица включает Ubuntu/Debian, `amd64` и минимум один `arm64` образ:

- чистый bootstrap, повторная установка и update;
- timezone, locale, packages и swap;
- SSH migration с активной старой сессией;
- неуспешный новый порт с сохранением порта 22;
- успешный новый порт и отдельное закрытие 22;
- reboot после каждого опасного этапа;
- user lifecycle, SSH login и sudo policy;
- system VPN, rollback и kill switch;
- два пользователя: VPN и direct;
- TCP, UDP, DNS и IPv6 leak tests;
- сохранение management-доступа при недоступном VPN endpoint.

## 18. Критерии приёмки

Функция готова, если:

1. Чистый поддерживаемый сервер регистрируется одним bootstrap-кодом без передачи приватных ключей.
2. Повторная базовая настройка не повреждает систему.
3. Порт 22 невозможно закрыть до успешного второго SSH-подключения.
4. Ошибка SSH/firewall сохраняет доступ либо завершает rollback.
5. Новый пользователь создаётся без sudo, permissions SSH проходят проверку.
6. System VPN меняет внешний IP и откатывается при ошибке probes.
7. Трафик назначенного UID идёт через VPN, контрольный UID — напрямую.
8. Kill switch не допускает прямой выход после остановки Xray.
9. TCP, UDP, DNS и IPv6 probes соответствуют выбранной policy.
10. Пароли, plaintext subscription URL и private keys отсутствуют в SQLite, logs и diagnostics.
11. Все опасные операции имеют audit, idempotency, verification и rollback result.
12. После reboot factual state соответствует последнему подтверждённому desired state.
13. Bootstrap, helper, schemas и manifest получены только из release этого репозитория.

## 19. Этапы реализации

### A — собственный bootstrap и release contract

- bootstrap в этом репозитории;
- pinned release manifest, checksums и compatibility matrix;
- non-interactive helper actions и JSON schemas;
- install/update/rollback/uninstall;
- CI для Ubuntu/Debian и supported architectures.

### B — Provisioning control plane

- models и SQLite migrations;
- API, state machine, confirmations и events;
- Agent job channel и reconciliation;
- restricted Unix-socket helper;
- retention и diagnostics.

### C — базовая настройка и пользователи

- Desktop wizard;
- base setup;
- user lifecycle;
- logs, retry, verification и rollback.

### D — firewall и SSH

- firewall editor;
- two-phase SSH migration;
- Desktop connectivity probe;
- отдельное закрытие старого порта.

### E — системный Xray VPN

- Node-encrypted profiles;
- Xray lifecycle;
- routing exclusions, rollback timer и leak tests;
- management emergency recovery.

### F — VPN для пользователя

- UID/cgroup routing;
- TCP/UDP/DNS/IPv6 policy;
- per-user kill switch;
- assignment UI и reconciliation.

### G — hardening и alpha release

- полная VM matrix;
- update/rollback/reboot tests;
- threat-model review;
- локализация UI и документации;
- физическая приёмка на тестовых серверах.
