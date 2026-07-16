# Контракт Linux-установщика

Единственный исходный файл `ochenstarik-server-monitor-manager.sh` хранится в `ochenstarik-ui/lightweight-server`. В репозитории desktop client не должна находиться устаревающая копия.

## Поддерживаемые роли

### Monitor only

Режим `install` устанавливает SSH monitoring endpoint без WireGuard:

- Ubuntu/Debian с systemd;
- публичный ключ `ssh-ed25519` из Windows-клиента;
- отдельный системный пользователь `ochenstarik-monitor` без пароля;
- root-owned forced-command;
- сохранение существующего SSH-порта;
- отсутствие нового публичного API.

### Hub

Режим `hub` дополнительно:

- устанавливает WireGuard и nftables;
- запрашивает публичный IPv4/домен и UDP-порт;
- создаёт `smm0` с адресом `10.77.0.1/24`;
- включает IPv4 forwarding;
- устанавливает минимальный root helper и systemd restore unit;
- хранит публичные identities Node и политики Links;
- разрешает транзит только по явной политике.

### Node

Режим `node`:

- локально генерирует WireGuard keypair;
- принимает одноразовый enrollment token;
- отправляет Hub только публичный ключ;
- получает внутренний адрес и подписанную конфигурацию;
- создаёт только исходящее WireGuard-соединение;
- не требует белого IP или входящего публичного порта.

## Команды жизненного цикла

Целевой интерфейс:

```text
install-monitor
install-hub
install-node
status
update
rollback
uninstall-monitor
uninstall-node
uninstall-hub
```

Каждая установка и обновление должны быть идемпотентными. Перед изменением рабочей конфигурации создаётся root-only backup. Ошибка проверки или запуска автоматически восстанавливает последнюю рабочую версию.

Удаление роли должно убрать только принадлежащие ей файлы, units, интерфейсы и правила. Удаление Hub требует отдельного подтверждения и не должно молча оставлять включённый forwarding или nftables ACL.

## Forced-command

Ключ мониторинга допускает только:

- `metrics`;
- read-only `mesh nodes`, `mesh links`, `mesh status` на Hub;
- строго типизированные изменения Link с проверкой параметров.

Он не должен позволять shell, PTY, agent forwarding, TCP forwarding или произвольную команду. Полный SSH-терминал использует отдельную identity.

## Минимальный metrics snapshot

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

## Проверки перед применением

- `bash -n` и ShellCheck;
- проверка SSH-ключа через `ssh-keygen`;
- `sshd -t` перед reload;
- `wg-quick strip` и пробный запуск конфигурации;
- `nft --check` перед заменой таблицы;
- проверка systemd unit;
- сохранение активной SSH-сессии до подтверждения нового доступа.
