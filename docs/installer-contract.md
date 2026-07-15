# Контракт Linux-установщика

Файл `ochenstarik-server-monitor-manager.sh` находится в `ochenstarik-ui/lightweight-server`; синхронная копия хранится в `deploy/` этого репозитория.

## Первый тестовый этап: SSH pull

До появления постоянного агента Windows-клиент получает метрики через OpenSSH. Установщик:

- поддерживает Ubuntu/Debian с systemd;
- запрашивает публичный ключ `ssh-ed25519`, созданный Windows-клиентом;
- читает интерактивный ключ из `/dev/tty`, поэтому поддерживает установку через `curl | sudo bash`;
- поддерживает повторяемый режим через `SERVER_MONITOR_PUBLIC_KEY`;
- проверяет синтаксис ключа через `ssh-keygen`;
- устанавливает и включает OpenSSH Server, не меняя текущий SSH-порт;
- создаёт отдельного системного пользователя `ochenstarik-monitor` без пароля;
- устанавливает root-owned metrics command;
- записывает ключ как `restrict,command="..."`;
- не создаёт UFW-правил и не открывает дополнительных портов;
- поддерживает `install`, `status` и `uninstall`;
- допускает безопасный повторный запуск для замены ключа мониторинга.

Forced-command отдаёт только строки `KEY=VALUE`:

```text
PROTOCOL=1
HOSTNAME=server-name
UPTIME_SECONDS=12345
LOAD1=0.42
CPU_COUNT=4
MEM_TOTAL_KB=...
MEM_AVAILABLE_KB=...
DISK_TOTAL_KB=...
DISK_AVAILABLE_KB=...
KERNEL=...
```

Ключ мониторинга не должен позволять shell, PTY, agent forwarding, TCP forwarding или выполнение переданной клиентом команды. Полноценный SSH-терминал использует отдельную пользовательскую identity и не входит в этот ключ.

## Будущий этап: постоянный агент

После проверки UX на нескольких серверах SSH pull будет дополнен статическим агентом для потоковых метрик, systemd events, короткого локального буфера и управляемых WireGuard Links. Агент будет регистрироваться одноразовым token и работать по исходящему mTLS-соединению без входящего API.
