# Linux bootstrap: первая тестовая версия

Собственный bootstrap Server Monitor Manager устанавливает Control или Agent из локального release-архива. Архив принимается только вместе с файлом `<archive>.sha256`, проверяется до распаковки и может содержать лишь каталоги `agent`, `control`, `deploy` и `bootstrap`.

Текущая версия предназначена для подготовки следующего alpha release. Она устанавливает Control/Agent и mTLS enrollment, но пока не настраивает WireGuard data plane. Ограниченный policy helper намеренно отклоняет Link mutations до реализации следующего этапа.

## Поддерживаемые системы

- Ubuntu Server 22.04/24.04;
- Debian 12/13;
- `amd64` и `arm64`;
- systemd.

## Установка Control

Скачайте из одного release:

- `ochenstarik-server-monitor-manager.sh` и его `.sha256`;
- `server-monitor-manager-linux-x64.tar.gz` или `server-monitor-manager-linux-arm64.tar.gz`;
- соответствующий `.tar.gz.sha256`.

Сначала проверьте bootstrap, затем установите Control. `PUBLIC_HOST` должен совпадать с DNS-именем или IP, по которому Agents обращаются к Hub:

```bash
sha256sum -c ochenstarik-server-monitor-manager.sh.sha256
chmod 700 ochenstarik-server-monitor-manager.sh
sudo ./ochenstarik-server-monitor-manager.sh preflight
sudo ./ochenstarik-server-monitor-manager.sh install-control \
  ./server-monitor-manager-linux-x64.tar.gz \
  hub.example.com \
  7443
```

Bootstrap создаёт собственный Control CA и HTTPS certificate, системного пользователя, root-only configuration, SQLite directory и hardened systemd unit. В конце он показывает SHA-256 fingerprint CA.

Создайте десятиминутный самодостаточный код `SMMNODE1` для Node:

```bash
sudo ./ochenstarik-server-monitor-manager.sh node-code home
sudo ./ochenstarik-server-monitor-manager.sh control-ca-fingerprint
```

Код содержит URL Control, Node ID, одноразовый token и только публичный CA certificate. Приватный CA PFX остаётся на Hub.

## Установка Agent

Вставьте код `SMMNODE1` в скрытый prompt. Bootstrap покажет fingerprint CA и потребует сравнить его с Hub до создания локального Agent key и CSR. Token не сохраняется в `agent.env`:

```bash
sudo ./ochenstarik-server-monitor-manager.sh install-node \
  ./server-monitor-manager-linux-x64.tar.gz
```

Для автоматизированного теста код и явное принятие уже сверенного fingerprint можно передать только в окружении одного процесса:

```bash
sudo SMM_ENROLL_CODE='SMMNODE1....' SMM_ACCEPT_CA_FINGERPRINT=1 \
  ./ochenstarik-server-monitor-manager.sh install-node \
  ./server-monitor-manager-linux-x64.tar.gz
```

## Жизненный цикл

```bash
sudo ./ochenstarik-server-monitor-manager.sh status
sudo ./ochenstarik-server-monitor-manager.sh update-control ARCHIVE
sudo ./ochenstarik-server-monitor-manager.sh update-agent ARCHIVE
sudo ./ochenstarik-server-monitor-manager.sh rollback control
sudo ./ochenstarik-server-monitor-manager.sh rollback agent
sudo ./ochenstarik-server-monitor-manager.sh uninstall-agent
sudo ./ochenstarik-server-monitor-manager.sh uninstall-agent --purge
sudo ./ochenstarik-server-monitor-manager.sh uninstall-control --confirm-destroy-control
```

Update создаёт root-only backup перед заменой binaries. При неуспешном health state предыдущая версия восстанавливается. Agent uninstall без `--purge` сохраняет state; удаление Control требует явного подтверждения и удаляет его SQLite/CA state.

## Следующий этап

До тестирования Mesh необходимо добавить WireGuard Hub/Node setup, выдачу адресов, persistent keepalive и применение nftables Links в policy helper. До этого момента bootstrap подходит для тестирования Control, Agent, enrollment, heartbeat, update и rollback, но не для сетевого объединения серверов.
