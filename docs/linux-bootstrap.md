# Linux bootstrap и собственная Mesh-сеть

Server Monitor Manager устанавливает Control (Hub) и Agent (Node) из проверяемого release-архива. Для связи серверов используется собственная WireGuard-сеть `10.77.0.0/24`; Tailscale и другие внешние VPN-сервисы не требуются.

Текущая версия предназначена для alpha-тестирования на Ubuntu Server 22.04/24.04 и Debian 12/13 (`amd64`, `arm64`, systemd). Hub должен иметь публичный IPv4-адрес или DNS-имя и доступный UDP-порт. Node может находиться за NAT без белого IP.

## Быстрая установка

Скачайте и проверьте convenience installer из `v0.1.0-alpha.15`:

```bash
curl -fsSLO https://github.com/ochenstarik-ui/server-monitor-manager/releases/download/v0.1.0-alpha.15/smm-setup.sh
curl -fsSLO https://github.com/ochenstarik-ui/server-monitor-manager/releases/download/v0.1.0-alpha.15/smm-setup.sh.sha256
sha256sum -c smm-setup.sh.sha256
chmod 700 smm-setup.sh
```

На Hub установка Control и инициализация Mesh выполняются одной командой. Порты можно не указывать: по умолчанию используются HTTPS `7443` и WireGuard `51820`.

```bash
sudo ./smm-setup.sh install-hub hub.example.com 7443 51820
```

На Node команда сама выбирает `linux-x64` или `linux-arm64`, скачивает и проверяет релиз, затем запрашивает код `SMMNODE2`:

```bash
sudo ./smm-setup.sh install-node
```

Обе команды публично скачивают архив, его checksum, manifest, подпись и Fulcio-сертификат из одного неизменяемого тега. Отсутствие любого файла подписи прерывает установку; автоматического перехода на unsigned-режим нет. Опции `--tag` и `--repository` предназначены для явного выбора другого источника. Остальные команды bootstrap по-прежнему можно передавать напрямую; `-- COMMAND` принудительно включает сквозной режим.

## Ручная установка и файлы релиза

Скачайте из одного GitHub Release:

- `ochenstarik-server-monitor-manager.sh` и `.sha256`;
- `server-monitor-manager-linux-x64.tar.gz` или `server-monitor-manager-linux-arm64.tar.gz`;
- соответствующий `.tar.gz.sha256`;
- `server-monitor-manager-manifest.json`, `server-monitor-manager-manifest.sig` и `server-monitor-manager-manifest.pem`.

Все файлы должны лежать в одном каталоге: `verify-release` ищет manifest, подпись и сертификат рядом с архивом. Без любого из трёх проверка отказывает и предлагает явный обход `SMM_ALLOW_UNSIGNED=1` — он предназначен только для сборок, выпущенных до появления подписи, и в обычной установке не используется.

Сертификат нужен потому, что manifest подписывается keyless-режимом cosign: подпись проверяется эфемерным сертификатом, привязанным к workflow выпуска, а не постоянным ключом.

Bootstrap сам обеспечивает `cosign`, если пригодный бинарь ещё не найден в `PATH`. Поставка закреплена на `cosign v3.1.3`: установщик публично скачивает официальный `cosign-linux-amd64` или `cosign-linux-arm64`, проверяет SHA-256 до первого запуска и устанавливает проверенный файл как `/usr/local/bin/cosign` с режимом `0755`. Уже установленный `cosign` не заменяется, но должен успешно выполнять `cosign version`.

Закреплённые контрольные суммы:

- `cosign-linux-amd64`: `4629c757b7618056f8ddd7e2625ae9fdd94c0372a65049520bc7d9df9efc7f71`;
- `cosign-linux-arm64`: `c5d324e091826b0d7a78eb16fef316450b4eb9aaec045611c08ba06f5e73220a`.

Смена версии или любой из сумм является осознанным обновлением поставки и должна проходить через новый релиз. Если GitHub недоступен, установка останавливается с сообщением, содержащим версию, ожидаемую сумму, URL и путь `/usr/local/bin/cosign`, чтобы оператор мог вручную поставить ровно этот бинарь и проверить его до запуска.

Bootstrap проверяет подпись manifest, затем SHA-256 архива по manifest, и принимает в архиве только каталоги `agent`, `control`, `deploy` и `bootstrap`.

## 1. Ручная установка главного сервера (Hub)

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

## Локальное аварийное восстановление

Установщик размещает независимую от Control Hub команду `/usr/local/sbin/ochenstarik-smm-emergency`. Она принимает только фиксированные действия и управляет исключительно интерфейсом `smm0`, systemd units и nftables-таблицей Server Monitor Manager:

```bash
sudo ochenstarik-smm-emergency status
sudo ochenstarik-smm-emergency mesh-disable
sudo ochenstarik-smm-emergency firewall-restore
sudo ochenstarik-smm-emergency mesh-enable
```

`mesh-disable` останавливает WireGuard, удаляет только таблицу `inet ochenstarik_smm` и ставит локальный emergency marker, не останавливая Control, Agent или SSH. `firewall-restore` восстанавливает базовую политику deny-by-default и атомарно создаёт root-only запрос реконсиляции с уникальным generation. Control немедленно выполняет первый фоновый проход после старта, затем одним `link-list` сверяет фактические managed-правила со всеми последними Link-политиками не реже настроенного интервала `Control__LinkReconciliationSeconds` (по умолчанию 300 секунд). Новый marker запускает до трёх внеочередных проходов на следующих poll tick; после трёх отказов обычный throttle восстанавливается, marker сохраняется, а журнал получает одно предупреждение с id проблемных политик. Marker не обходит backoff недоступного firewall. Завершённые политики `Disabled/Disabled` удаляются вместе со своей историей после `Control__LinkRetentionDays` (по умолчанию 90 дней); действующие политики и расхождения retention не затрагивает. Helper удаляет только тот generation, который был прочитан перед успешно завершившимся проходом; более новый запрос сохраняется. При недоступной таблице запрос остаётся для retry с backoff, а Desktop показывает единый баннер «Mesh firewall не загружен». Если firewall не удаётся восстановить, команда отключает Mesh для fail-closed результата. `mesh-enable` также создаёт запрос реконсиляции; запускайте его только после проверки конфигурации и доступности Hub.
