# Контракт собственного Linux bootstrap

## 1. Владение и поставка

Bootstrap, helper, systemd units, JSON schemas и manifests являются компонентами Server Monitor Manager и хранятся только в этом репозитории. Они публикуются одним совместимым release вместе с Desktop, Control и Agent.

Bootstrap не скачивает и не запускает исходники других проектов. Production-установка использует закреплённый release/tag, проверяет signed compatibility manifest и SHA-256 каждого artifact. Mutable `main` не является источником production-установки.

Пока bootstrap не опубликован в release, документация не должна предлагать несуществующую команду его скачивания.

## 2. Поддерживаемые роли

### Monitor

- Ubuntu/Debian с systemd;
- отдельный `ochenstarik-monitor` без пароля;
- публичный Ed25519 key из Desktop;
- root-owned forced command;
- сохранение существующего SSH-порта;
- запрет shell, PTY и forwarding.

### Control Hub

- ASP.NET Core Control service и SQLite;
- локальный Control CA и HTTPS certificate;
- TCP `7443` по умолчанию;
- WireGuard interface и root-owned nftables policy helper;
- systemd units и root-only state directories;
- транзит только по explicit directional Link.

### Node

- локальная генерация Agent и WireGuard private keys;
- CSR-based enrollment по одноразовому коду;
- только исходящие mTLS/WireGuard sessions;
- отсутствие требования публичного IP и входящего порта;
- restricted provisioning helper без общего root shell.

## 3. Enrollment

1. Пользователь скачивает bootstrap и checksum из release Server Monitor Manager.
2. Запускает bootstrap локально через `sudo`.
3. Сверяет fingerprint Control CA.
4. Вводит одноразовый enrollment code.
5. Node локально создаёт key и CSR.
6. Control выдаёт role-scoped certificate.
7. Bootstrap устанавливает совместимые Agent/helper units.
8. Enrollment code атомарно погашается.

Sudo-пароль не передаётся в Desktop, Control или audit. Приватные Node keys не покидают Node. Приватный Control CA key не включается в enrollment code.

## 4. Root helper

Helper доступен только через root-owned Unix socket или фиксированный non-interactive privilege wrapper. Он принимает:

- известный action id;
- schema version;
- JSON, соответствующий строгой схеме;
- job id и module hash.

Helper не принимает shell text, произвольные paths, environment или неизвестные поля, способные изменить смысл операции. Username, UID, port, protocol, CIDR, timezone, package id и управляемые пути валидируются повторно.

Каждая mutation:

1. выполняет preflight;
2. создаёт root-only backup;
3. отклоняет symlink в managed path;
4. проверяет синтаксис новой конфигурации;
5. применяет изменение атомарно;
6. проверяет factual state;
7. при ошибке выполняет rollback.

## 5. Целевой CLI

```text
bootstrap enroll
bootstrap status
bootstrap update
bootstrap rollback BACKUP_ID
bootstrap uninstall ROLE
control device-code DEVICE_ID
control node-code NODE_ID
control automation-token AUTOMATION_ID SOURCE_NODE_ID
emergency status
emergency vpn-disable
emergency ssh-restore BACKUP_ID
emergency firewall-restore BACKUP_ID
```

CLI является non-interactive, кроме локального ввода enrollment code и явных подтверждений опасного удаления. Машиночитаемый режим возвращает versioned JSON и стабильные exit codes.

## 6. Идемпотентность и обновление

- повторная установка не дублирует users, keys, units, routes или firewall rules;
- несовместимая версия Control/Agent/helper блокирует provisioning job;
- update загружает artifacts только из release этого репозитория;
- checksum проверяется до остановки service;
- бинарники и units заменяются атомарно;
- неуспешный health check восстанавливает предыдущую версию;
- uninstall удаляет только принадлежащие выбранной роли files, users, interfaces и rules;
- удаление Hub требует отдельного подтверждения и не оставляет forwarding/ACL.

## 7. Forced command monitoring

Monitoring key допускает только versioned metrics snapshot и read-only mesh status. Полный SSH-терминал использует отдельную пользовательскую identity.

Минимальный snapshot:

```text
PROTOCOL=1
HOSTNAME=server-name
UPTIME_SECONDS=12345
LOAD1=0.42
CPU_COUNT=4
MEM_TOTAL_KB=...
MEM_AVAILABLE_KB=...
SWAP_TOTAL_KB=...
SWAP_FREE_KB=...
DISK_TOTAL_KB=...
DISK_AVAILABLE_KB=...
DISK_INODES_TOTAL=...
DISK_INODES_FREE=...
NETWORK_RX_BYTES=...
NETWORK_TX_BYTES=...
KERNEL=...
```

## 8. Обязательные проверки

- ShellCheck и `bash -n` для bootstrap scripts;
- `ssh-keygen` для public keys;
- `sshd -t` и `sshd -T` до reload;
- `wg-quick strip` для WireGuard configuration;
- `nft --check` до замены managed rules;
- проверка systemd units;
- проверка active session до миграции SSH;
- checksum, permissions и ownership release artifacts;
- repeated install/update/rollback/uninstall;
- reboot на поддерживаемой VM matrix;
- сохранение management-доступа при helper/VPN failure.

Полные требования к заданиям, настройке ОС, пользователям и Xray приведены в [ТЗ Provisioning и Xray VPN](provisioning-vpn-requirements.md).
