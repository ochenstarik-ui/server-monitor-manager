# Linux bootstrap и собственная Mesh-сеть

Server Monitor Manager устанавливает Control (Hub) и Agent (Node) из проверяемого release-архива. Для связи серверов используется собственная WireGuard-сеть `10.77.0.0/24`; Tailscale и другие внешние VPN-сервисы не требуются.

Текущая версия предназначена для alpha-тестирования на Ubuntu Server 22.04/24.04 и Debian 12/13 (`amd64`, `arm64`, systemd). Hub должен иметь публичный IPv4-адрес или DNS-имя и доступный UDP-порт. Node может находиться за NAT без белого IP.

## Файлы релиза

Скачайте из одного GitHub Release:

- `ochenstarik-server-monitor-manager.sh` и `.sha256`;
- `server-monitor-manager-linux-x64.tar.gz` или `server-monitor-manager-linux-arm64.tar.gz`;
- соответствующий `.tar.gz.sha256`.

Bootstrap проверяет SHA-256 до распаковки и принимает в архиве только каталоги `agent`, `control`, `deploy` и `bootstrap`.

## 1. Установка главного сервера (Hub)

```bash
sha256sum -c ochenstarik-server-monitor-manager.sh.sha256
chmod 700 ochenstarik-server-monitor-manager.sh
sudo ./ochenstarik-server-monitor-manager.sh preflight
sudo ./ochenstarik-server-monitor-manager.sh verify-release \
  ./server-monitor-manager-linux-x64.tar.gz
sudo ./ochenstarik-server-monitor-manager.sh install-control \
  ./server-monitor-manager-linux-x64.tar.gz \
  hub.example.com \
  7443
sudo ./ochenstarik-server-monitor-manager.sh mesh-init hub.example.com 51820
```

Откройте на Hub и во внешнем firewall/security group:

- TCP `7443` для Control HTTPS;
- UDP `51820` для WireGuard.

`install-control` создаёт локальный Control CA и HTTPS-сертификат. Приватный ключ CA не включается в коды подключения и остаётся на Hub. `mesh-init` устанавливает `wireguard-tools`, `nftables` и `iproute2`, создаёт ключ Hub, интерфейс `smm0`, включает IPv4 forwarding и межсерверный firewall с запретом по умолчанию.

## 2. Выпуск кода для Node

```bash
sudo ./ochenstarik-server-monitor-manager.sh node-code home
sudo ./ochenstarik-server-monitor-manager.sh control-ca-fingerprint
```

После `mesh-init` команда выдаёт одноразовый код `SMMNODE2`. Он содержит Control URL, публичный сертификат CA, Node ID, десятиминутный enrollment token, endpoint и публичный ключ Hub, а также зарезервированный Mesh-адрес. Обращайтесь с кодом как с временным секретом.

## 3. Установка вторичного сервера (Node)

Node может быть за NAT. Ему нужен исходящий доступ к TCP-порту Control и UDP-порту WireGuard на Hub.

```bash
sudo ./ochenstarik-server-monitor-manager.sh install-node \
  ./server-monitor-manager-linux-x64.tar.gz
```

Вставьте `SMMNODE2` в скрытый prompt. Сверьте показанный SHA-256 fingerprint CA с Hub и введите `yes`. После mTLS enrollment установщик создаст локальный приватный ключ WireGuard, запустит `smm0` с `PersistentKeepalive = 25` и выведет публичный код `SMMPEER1`.

Для автоматизированного стенда допускается передача кода только в окружении процесса после отдельной сверки fingerprint:

```bash
sudo SMM_ENROLL_CODE='SMMNODE2....' SMM_ACCEPT_CA_FINGERPRINT=1 \
  ./ochenstarik-server-monitor-manager.sh install-node \
  ./server-monitor-manager-linux-x64.tar.gz
```

## 4. Активация Node на Hub

Скопируйте выведенный Node код `SMMPEER1` на Hub:

```bash
sudo ./ochenstarik-server-monitor-manager.sh peer-add 'SMMPEER1....'
sudo ./ochenstarik-server-monitor-manager.sh mesh-status
```

Hub проверяет Node ID и ранее зарезервированный IP, сохраняет публичный ключ peer и перезапускает интерфейс. Приватный ключ Node никогда не покидает Node.

## Изоляция и управляемые соединения

Трафик `smm0 -> smm0` по умолчанию блокируется. Control вызывает root-helper только для точных правил `source IP -> target IP`, протокола и порта. Поддерживаются команды helper `link-connect SOURCE TARGET tcp|udp PORT TTL_MINUTES` и `link-disconnect SOURCE TARGET tcp|udp PORT`. Это позволяет вручную подключать AI-агент к выбранному серверу и затем отзывать доступ, не открывая связь между всеми Node.

В текущем alpha TTL валидируется и хранится Control, а удаление просроченных правил зависит от reconciliation Control. После перезапуска firewall разрешающие правила должны быть повторно применены Control.

## Обслуживание

```bash
sudo ./ochenstarik-server-monitor-manager.sh status
sudo ./ochenstarik-server-monitor-manager.sh mesh-status
sudo ./ochenstarik-server-monitor-manager.sh update-control ARCHIVE
sudo ./ochenstarik-server-monitor-manager.sh update-agent ARCHIVE
sudo ./ochenstarik-server-monitor-manager.sh rollback control
sudo ./ochenstarik-server-monitor-manager.sh rollback agent
sudo ./ochenstarik-server-monitor-manager.sh uninstall-agent
sudo ./ochenstarik-server-monitor-manager.sh uninstall-agent --purge
sudo ./ochenstarik-server-monitor-manager.sh uninstall-control --confirm-destroy-control
```

Update создаёт root-only backup перед заменой binaries и автоматически восстанавливает предыдущую версию, если сервис не запускается. Перед alpha-тестом на реальных серверах обязательно сохраните отдельную консольную/SSH-сессию и не закрывайте основной административный доступ firewall-правилами проекта.
