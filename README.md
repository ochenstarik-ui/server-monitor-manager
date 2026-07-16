# Server Monitor Manager

Лёгкая система мониторинга и безопасного управления Linux-серверами с одного ПК. Первый клиент создаётся для Windows; после стабилизации протокола появятся клиенты для других настольных систем и мобильных устройств.

## Основные задачи

- единая панель для нескольких личных и рабочих серверов;
- CPU/load, память, корневой диск, задержка и uptime через ограниченный SSH forced-command;
- SSH-терминал и сохранённые подключения без передачи приватных ключей серверу управления;
- группы серверов, теги, избранное и сводные предупреждения;
- явное включение и ручное отключение защищённых связей между серверами;
- ограниченный доступ любого AI-агента к выбранному серверу разработки;
- маленький Linux-агент без тяжёлой панели, Docker и отдельной базы данных на каждом узле.

## Архитектура

```text
Windows client -- ограниченный SSH --> главный WireGuard Hub с белым IP
                                            ^
                                            | исходящие WireGuard-соединения
                                            |
                            AI-агент / Home / другие серверы
```

Hub объединяет узлы в приватной подсети, но по умолчанию запрещает транзит между ними. Каждая связь `источник → цель` включается и отключается отдельно. Поэтому можно оставить `AI-агент → Home`, отключив только `AI-агент → Server2`. Обратное направление не появляется автоматически.

Подробности: [архитектура](docs/architecture.md), [модель безопасности](docs/security-model.md), [план разработки](docs/roadmap.md) и [контракт Linux-установщика](docs/installer-contract.md).

## Планируемая структура

```text
src/
  ServerMonitorManager.Desktop/   WinUI 3, Windows-first
  ServerMonitorManager.Core/      общие модели и сценарии
  ServerMonitorManager.Agent/     лёгкий Linux-агент
  ServerMonitorManager.Control/   API, события, аудит и координация
docs/
deploy/
tests/
```

## Текущий статус

Проект находится на стадии первого тестируемого Windows MVP. Репозиторий переименован из `mobile-server-manager`, потому что первой целевой платформой теперь является Windows. Клиент создаёт отдельный Ed25519-ключ, сохраняет профили локально и получает живые метрики серверов через системный OpenSSH.

Установочный скрипт Linux-части хранится в репозитории [`ochenstarik-ui/lightweight-server`](https://github.com/ochenstarik-ui/lightweight-server) под именем `ochenstarik-server-monitor-manager.sh`. Он устанавливает режим Hub или Node, создаёт отдельного пользователя `ochenstarik-monitor` и SSH forced-command: ключ приложения не получает shell, PTY, port forwarding или право выполнять произвольные команды.

В разработческой ветке также появился первый срез постоянного control layer: ASP.NET Core 10 Hub, SQLite и исходящий Linux Agent. Регистрация использует одноразовый token и CSR, дальнейшие heartbeat-запросы — mTLS. Этот слой ещё не заменяет проверенный SSH/WireGuard установщик: сначала будут добавлены release-бинарники, systemd-установка и миграция Links.

Alpha-установка постоянного слоя после обычных ролей Hub/Node:

```bash
# Hub
sudo ./ochenstarik-server-monitor-manager.sh install-control-hub
sudo ./ochenstarik-server-monitor-manager.sh control-code home

# соответствующий Node — вставить полученный SMMCTL1
sudo ./ochenstarik-server-monitor-manager.sh install-control-agent
```

Архив выбирается автоматически для amd64 или arm64 и проверяется по SHA-256. Для Control Hub требуется входящий TCP-порт `7443`; Agent открытых входящих портов не создаёт.

Проверка control layer для разработчиков:

```bash
dotnet build ServerMonitorManager.slnx --configuration Release
dotnet test tests/ServerMonitorManager.Control.Tests/ServerMonitorManager.Control.Tests.csproj --configuration Release
```

## Быстрый тест на трёх серверах

1. Запустите Windows-клиент и нажмите `SSH-ключ` → `Копировать`.
2. На сервере с белым IP скачайте установщик и выберите режим Hub:

```bash
curl -fLO https://raw.githubusercontent.com/ochenstarik-ui/lightweight-server/agent/server-monitor-installer/ochenstarik-server-monitor-manager.sh
chmod 700 ochenstarik-server-monitor-manager.sh
sudo ./ochenstarik-server-monitor-manager.sh hub
```

3. Откройте выбранный UDP-порт WireGuard (по умолчанию `51820`) и создайте коды узлов:

```bash
sudo ochenstarik-smm node-code ai-agent
sudo ochenstarik-smm node-code home
sudo ochenstarik-smm node-code server2
```

4. На каждом вторичном сервере запустите `sudo ./ochenstarik-server-monitor-manager.sh node`, вставьте SSH-ключ приложения и код именно этого узла. Node локально создаст WireGuard-ключ и покажет запрос `SMMREQ1`.
5. На Hub выполните `sudo ochenstarik-smm node-enroll`, вставьте запрос по скрытому приглашению и верните полученный `SMMACK1` в установщик Node. Белый IP вторичным серверам не нужен.
6. В приложении добавьте главный сервер с пользователем `ochenstarik-monitor` и отметьте `Это главный Mesh Hub`.
7. Нажмите обновление в панели связей, выберите источник и цель, затем `Разрешить` или `Отключить`.

Приватный ключ хранится в локальном каталоге packaged-приложения Windows. Первый SSH-host key принимается в режиме `accept-new`, затем проверяется по отдельному `known_hosts` приложения.

Для предварительной проверки вместо запуска через pipe скачайте файл, выполните `bash -n` и просмотрите его содержимое.

Enrollment-код действует 10 минут и погашается после первой успешной регистрации. Приватный WireGuard-ключ создаётся и остаётся только на Node.

## Принципы безопасности

- никаких общих root-паролей и приватных SSH-ключей на Hub;
- отдельный ключ мониторинга и pinned `known_hosts`; код регистрации Node передаётся один раз вручную;
- отдельная идентичность для пользователя, устройства, агента и автоматизации;
- опасные действия требуют повторного подтверждения и попадают в неизменяемый аудит;
- доступ AI-агенту выдаётся отдельному Unix-пользователю и SSH-ключу с минимальными правами;
- отключение Link сразу удаляет разрешённую пару из nftables на Hub, не затрагивая другие связи узла.

## Лицензия и секреты

Лицензия будет выбрана до публикации первого исходного релиза. Токены регистрации, ключи WireGuard, SSH-ключи, сертификаты устройств и production-конфигурация не должны попадать в Git.
