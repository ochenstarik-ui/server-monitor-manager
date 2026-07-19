# План разработки

Подробные требования к собственному bootstrap, управляемой настройке Linux и Xray находятся в [ТЗ Provisioning и Xray VPN](provisioning-vpn-requirements.md). Все компоненты Server Monitor Manager разрабатываются, версионируются и выпускаются только в этом репозитории.

Текущее автоматическое покрытие поддерживаемых Linux-платформ описано в [Linux platform matrix](linux-platform-matrix.md).

## Этап 0 — граница и базовая архитектура

- [x] переименовать проект в Server Monitor Manager;
- [x] зафиксировать самостоятельность репозитория и отсутствие межрепозиторных runtime-зависимостей;
- [x] выбрать звёздную архитектуру Hub/Node для первого Mesh;
- [x] разделить monitoring, terminal, Agent, Operator и AI-automation identities;
- [x] описать направленные Links и kill switch;
- [x] выбрать Apache License 2.0;
- [x] добавить CI, форматирование, тесты и release checksums.

## Этап 1 — Windows SSH MVP

- [x] создать packaged WinUI 3 приложение;
- [x] добавить адаптивный Overview и профили нескольких серверов;
- [x] генерировать отдельный Ed25519 monitoring SSH key;
- [x] защищать monitoring key и Operator certificate через DPAPI;
- [x] сохранять профили локально без паролей;
- [x] получать CPU/load, RAM, disk, uptime и latency;
- [x] редактировать и удалять серверы;
- [x] поддерживать единственный изменяемый Mesh Hub;
- [x] добавить страницы Servers, Links, Sessions и Settings;
- [x] собирать и запускать x64-приложение;
- [ ] добавить группы, теги и избранные серверы;
- [ ] добавить настраиваемые alert rules и журнал уведомлений;
- [ ] добавить управляемую отдельную terminal key identity вместо зависимости только от обычных пользовательских SSH-ключей.

## Этап 2 — постоянный Control и Agent

- [x] ASP.NET Core Control Hub и SQLite;
- [x] self-contained Agent/Control binaries для `linux-x64` и `linux-arm64`;
- [x] исходящие mTLS Agent sessions;
- [x] одноразовые enrollment tokens не более чем на 10 минут;
- [x] локальная генерация Agent private key и CSR;
- [x] отдельные Agent, Operator и source-scoped Automation certificates;
- [x] отзыв и повторная регистрация Node/Operator;
- [x] подтверждение SHA-256 fingerprint Control CA;
- [x] защищённый event stream для Desktop;
- [x] ограниченный offline buffer Agent и downsampling;
- [x] idempotency и replay protection;
- [x] SQLite schema version, retention и backup/restore Control DB + CA;
- [ ] добавить Desktop UI управления Automation identities и токенами.

## Этап 3 — управляемые Links

- [x] направленные пары source -> destination;
- [x] управление Links из Windows-клиента;
- [x] политики по destination `/32`, TCP/UDP и порту;
- [x] TTL и фоновое автоматическое истечение;
- [x] состояния Connecting, Active, Disconnecting, Partial, Disabled и Failed;
- [x] версия политики и подтверждение применения helper;
- [x] обязательное восстановление disabled policy после reconnect;
- [x] append-only audit операций Link;
- [x] интеграционные тесты kill switch, process restart и helper failure;
- [ ] выполнить физический acceptance Hub + source Node + два destination Node с WireGuard/nftables/reboot.

## Этап 4 — мониторинг и терминал

- [x] CPU/load, RAM, swap, disk, inode, network и uptime;
- [x] состояния SSH/WireGuard и latency;
- [x] локальные предупреждения по ресурсам и доступности;
- [x] автоматическое обновление каждые 30 секунд;
- [x] локальная история до 240 точек на сервер;
- [x] графики CPU, RAM и диска;
- [x] экспорт redacted diagnostics;
- [x] прямой SSH-терминал с явным выбором пользователя;
- [x] source-scoped Automation API для AI-агента.

## Этап 5 — качество и нагрузка

- [x] HTTP authorization/integration tests;
- [x] Linux Agent parser tests;
- [x] Windows Desktop contract tests;
- [x] тест 100 Node: concurrent heartbeat, inventory и replay;
- [x] проверка TTL, retention, schema и backup/restore;
- [x] CI Windows/Linux и проверка форматирования;
- [ ] выполнить полную физическую приёмку по `docs/three-server-acceptance.md`;
- [ ] добавить долговременный soak test и измеримые performance budgets.

## Этап 6 — releases

- [x] GitHub prerelease с test-signed Windows MSIX;
- [x] SHA-256 для Windows package и Linux binaries;
- [x] self-contained Control/Agent release artifacts;
- [ ] настроить постоянную доверенную Windows code-signing identity;
- [ ] синхронизировать все переводы README с текущим release status.

## Этап 7 — собственный bootstrap Server Monitor Manager

- [x] добавить bootstrap source в этот репозиторий;
- [x] упаковывать bootstrap, checksum и compatibility manifest в release workflow;
- [ ] добавить криптографическую подпись compatibility manifest для production release;
- [x] проверять Ubuntu 22.04/24.04 и Debian 12/13, `amd64`/`arm64`;
- [x] добавить non-interactive install/update/rollback/uninstall;
- [x] устанавливать Control/Agent, restricted helper и systemd units;
- [x] локально создавать Agent key/CSR, выполнять mTLS enrollment и не сохранять token;
- [x] добавить собственную установку WireGuard Hub/Node и выдачу внутренних адресов;
- [x] реализовать nftables policy helper вместо временного deny-by-default helper;
- [x] добавить native Ubuntu 22.04/24.04 x64/arm64 VM CI и повторную установку;
- [x] добавить Debian 12/13 x64/arm64 systemd-container restart matrix;
- [ ] добавить полный reboot настоящих Debian VM;
- [x] добавить локальную emergency recovery command для текущих Mesh/firewall-компонентов.

## Этап 8 — Provisioning control plane

- [x] модели и SQLite migration v2 для ProvisioningJob;
- [x] state machine, confirmations, cancellation, retry и rollback;
- [x] создание, чтение, подтверждение и отмена через Operator API;
- [ ] выполнение, retry, verification и rollback в полной state machine;
- [x] обязательные idempotency key, audit reason и job TTL;
- [x] атомарный Agent job channel только для собственного `node_id`;
- [x] начальные строгие JSON schemas v1 для `preflight` и `system.base-install`;
- [ ] versioned JSON schemas для остальных action type;
- [x] restricted root helper через Unix socket (`preflight` и non-mutating plan для `system.base-install`);
- [x] двухфазный `system.base-install`: сохранённый проверенный plan до Operator confirmation;
- [x] structured redacted events, bounded Operator history и progress;
- [x] `NeedsReconciliation` после истечения execution TTL и неопределённого результата;
- [ ] desired/factual configuration и drift (`preflight` завершён; остальные action type ещё не подключены);
- [x] запрет параллельных активных заданий на одном Node (безопасный первый вариант).

## Этап 9 — базовая настройка и пользователи

- [ ] preflight ОС, архитектуры, SSH, firewall, APT и capabilities;
- [ ] Desktop wizard timezone/locale/packages/swap/unattended upgrades;
- [x] versioned package allowlist (catalog v1 с фиксированными package groups);
- [ ] root-only backups и symlink protection;
- [ ] user lifecycle без sudo по умолчанию;
- [ ] SSH public keys, fingerprints и permissions;
- [ ] sudo/passwordless sudo с отдельным подтверждением;
- [ ] block/unblock, sessions, services и безопасное удаление;
- [ ] verification и rollback для каждого action.

## Этап 10 — firewall и безопасная миграция SSH

- [ ] редактор managed firewall rules IPv4/IPv6/CIDR;
- [ ] preflight port conflict и effective sshd config;
- [ ] managed `sshd_config.d` drop-in;
- [ ] `sshd -t`, `sshd -T` и firewall dry-run;
- [ ] второе SSH-подключение Desktop на новом порту;
- [ ] сохранение host key fingerprint;
- [ ] запрет закрытия порта 22 до успешной проверки;
- [ ] отдельное подтверждение закрытия старого порта;
- [ ] автоматический rollback без разрыва активной сессии.

## Этап 11 — системный Xray VPN

- [ ] Node-specific encryption VPN subscription secrets;
- [ ] установка, update, disable и verification Xray;
- [ ] routing exclusions для Mesh, Control, SSH и VPN endpoint;
- [ ] automatic rollback timer;
- [ ] TCP/UDP/DNS/IPv6 probes и внешний IP до/после;
- [ ] system-wide kill switch с management exceptions;
- [ ] emergency disable без Control Hub;
- [ ] reboot/reconciliation tests.

## Этап 12 — Xray VPN для пользователя

- [ ] UID/cgroup marking, fwmark, policy route и TPROXY;
- [ ] один активный VPN profile на UID;
- [ ] TCP, UDP, DNS и IPv6 policy;
- [ ] per-user kill switch;
- [ ] probes от назначенного UID;
- [ ] исключение Xray, Mesh и management traffic;
- [ ] отключение assignment при смене UID/удалении пользователя;
- [ ] UI assignment и audit;
- [ ] VM test: один пользователь через VPN, второй напрямую.

## Этап 13 — hardening и дополнительные платформы

- [ ] threat-model review Provisioning и VPN;
- [ ] полная VM matrix и physical alpha acceptance;
- [ ] macOS и Linux Desktop после стабилизации Core/API;
- [ ] Android/iOS companion clients;
- [ ] push-уведомления без административных secrets у push provider.
