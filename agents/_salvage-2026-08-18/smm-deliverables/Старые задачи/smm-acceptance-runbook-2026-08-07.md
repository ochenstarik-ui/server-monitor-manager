# Установка SMM на три сервера с 3x-ui: предпроверка, установка, откат

Цель — закрыть физическую приёмку `tests/acceptance/three-server-mesh.sh`, не сломав работающий 3x-ui/Xray.

## 1. Распределение ролей

Скрипту нужны Hub и три Node-идентичности. На трёх серверах это собирается так — Hub совмещается с одним из Node:

| Сервер | Роль | Node ID | Требования |
|---|---|---|---|
| A — с публичным IP | Control Hub **+** Node | `home` | свободны TCP 7443 и UDP 51820 |
| B | Node, источник | `ai-agent` | исходящий доступ к A |
| C | Node, назначение | `second` | исходящий доступ к A |

Node B и C могут быть за NAT — входящие порты им не нужны.

Скрипт запускается с вашей рабочей машины (нужны `curl`, `jq`, `openssl`, `ssh`, `nc`, `base64`) и ходит по SSH только на A и B.

## 2. Что SMM создаёт на сервере

Всё именовано с префиксом проекта, чужого не трогает.

**Пользователи:** `ochenstarik-smm-control`, `ochenstarik-smm-agent` — системные, `nologin`, без пароля.

**Каталоги:** `/etc/ochenstarik-server-monitor-manager`, `/usr/local/lib/ochenstarik-server-monitor-manager`, `/var/lib/ochenstarik-server-monitor-manager`.

**systemd units:** `ochenstarik-smm-control.service`, `ochenstarik-smm-agent.service`, `ochenstarik-smm-provisioning-helper.service`, `ochenstarik-smm-firewall.service`, `wg-quick@smm0.service`.

**Файлы:** `/usr/local/libexec/ochenstarik-smm-policy-apply`, `/usr/local/sbin/ochenstarik-smm-emergency`, `/etc/sudoers.d/ochenstarik-smm-control` (разрешает control-пользователю запускать **только** policy-helper), `/etc/wireguard/smm0.conf`, `/etc/sysctl.d/90-ochenstarik-smm-mesh.conf`.

**nftables:** одна собственная таблица `inet ochenstarik_smm`. Все операции — только над ней.

**Пакеты:** `wireguard-tools`, `nftables`, `iproute2` — ставятся только если отсутствуют.

## 3. Чего SMM не трогает

- существующие таблицы nftables и правила iptables;
- конфигурацию и базу 3x-ui, конфиг Xray, его inbounds и сертификаты;
- `sshd_config` и текущий SSH-порт;
- существующих пользователей, их ключи и sudo;
- другие интерфейсы WireGuard — создаётся только `smm0`;
- маршруты по умолчанию.

Единственное системное изменение вне своего пространства имён — `net.ipv4.ip_forward=1`. Оно аддитивное: Xray работает в userspace и от этого не зависит, а если форвардинг уже включён, ничего не меняется.

## 4. Предпроверка — выполнить на каждом сервере до установки

```bash
echo "=== ОС и systemd ==="; . /etc/os-release; echo "$ID $VERSION_ID $(uname -m)"; ps -p 1 -o comm=
echo "=== занятые порты 7443 / 51820 ==="; ss -lntup | grep -E ':7443|:51820' || echo "свободны"
echo "=== существующие таблицы nftables ==="; nft list tables 2>/dev/null || echo "nft не установлен"
echo "=== iptables-legacy правила ==="; iptables-legacy -S 2>/dev/null | head -5 || echo "нет"
echo "=== интерфейсы WireGuard ==="; wg show interfaces 2>/dev/null || echo "нет"
echo "=== занята ли подсеть 10.77.0.0/24 ==="; ip -4 route | grep '10\.77\.' || echo "свободна"
echo "=== ip_forward ==="; sysctl -n net.ipv4.ip_forward
echo "=== порт 3x-ui и Xray ==="; ss -lntup | grep -Ei 'x-ui|xray' || echo "не найдено по имени"
echo "=== свободное место ==="; df -h / | tail -1
```

Поддерживаются Ubuntu 22.04/24.04 и Debian 12/13, `amd64`/`arm64`, обязателен systemd как PID 1.

**Останавливаться и не ставить, если:**

- 7443 или 51820 заняты — задать другие: HTTPS-порт передаётся третьим аргументом `install-control`, порт WireGuard — вторым аргументом `mesh-init`;
- подсеть `10.77.0.0/24` уже используется — она захардкожена в bootstrap, пересечение придётся разруливать до установки;
- ОС не из списка — `preflight` откажет сам.

## 5. Порядок установки

Сначала снимите снапшот каждого сервера у провайдера. Это дешевле любой отладки и делает весь раздел «откат» ненужным.

Держите открытой вторую SSH-сессию к каждому серверу на всём протяжении работ.

**Сервер A:**

```bash
sha256sum -c ochenstarik-server-monitor-manager.sh.sha256
chmod 700 ochenstarik-server-monitor-manager.sh
sudo ./ochenstarik-server-monitor-manager.sh preflight
sudo ./ochenstarik-server-monitor-manager.sh verify-release ./server-monitor-manager-linux-x64.tar.gz
sudo ./ochenstarik-server-monitor-manager.sh install-control ./server-monitor-manager-linux-x64.tar.gz <публичный-хост-A> 7443
sudo ./ochenstarik-server-monitor-manager.sh mesh-init <публичный-хост-A> 51820
```

Откройте во внешнем firewall провайдера TCP 7443 и UDP 51820. **Проверьте, что 3x-ui и Xray продолжают работать**, прежде чем идти дальше.

Дальше на A выпускается код для каждого Node, код вводится на соответствующем сервере, обратный код `SMMPEER1` возвращается на A через `peer-add`. Последовательность подробно описана в `docs/linux-bootstrap.md`, разделы 2–4. Сам A тоже регистрируется как Node `home`.

После каждого сервера — проверка, что 3x-ui жив.

## 6. Приёмка

```bash
export HUB_SSH_HOST=<A> HUB_SSH_USER=<пользователь>
export SOURCE_SSH_HOST=<B> SOURCE_SSH_USER=<пользователь>
export SSH_IDENTITY_FILE="$HOME/.ssh/id_ed25519"
export SOURCE_NODE_ID=ai-agent HOME_NODE_ID=home SECOND_NODE_ID=second
export HOME_WG_IP=10.77.0.2 SECOND_WG_IP=10.77.0.3
export TARGET_PORT=22
bash tests/acceptance/three-server-mesh.sh
```

Точные адреса `HOME_WG_IP` и `SECOND_WG_IP` возьмите из `mesh-status` на A.

Прогон в таком виде **не перезагружает серверы** и не восстанавливает бэкапы — он проверяет создание Links, отключение, истечение TTL и фактическую связность. Начните с него.

Полная приёмка добавляет два флага:

```bash
SMM_ACCEPT_RESTORE=1 SMM_ACCEPT_REBOOT=1 bash tests/acceptance/three-server-mesh.sh
```

`SMM_ACCEPT_REBOOT=1` **перезагружает A и B**. Для живого VPN это минуты недоступности — планируйте на низкий трафик. `SMM_ACCEPT_RESTORE=1` восстанавливает бэкап Control и перезапускает его; на 3x-ui не влияет.

Успех — строка `THREE_SERVER_ACCEPTANCE=PASS`.

## 7. Откат

```bash
sudo ochenstarik-server-monitor-manager.sh uninstall-agent --purge
sudo ochenstarik-server-monitor-manager.sh uninstall-control --confirm-destroy-control
sudo systemctl disable --now wg-quick@smm0
sudo rm -f /etc/wireguard/smm0.conf /etc/sysctl.d/90-ochenstarik-smm-mesh.conf
sudo nft delete table inet ochenstarik_smm 2>/dev/null || true
sudo sysctl --system
```

Аварийная команда, работающая без Control Hub, — на случай если mesh начнёт мешать:

```bash
sudo ochenstarik-smm-emergency status
sudo ochenstarik-smm-emergency mesh-disable
```

`mesh-disable` останавливает WireGuard и удаляет только таблицу проекта, не трогая Control, Agent, SSH и 3x-ui.

## 8. Честное предупреждение

Проект в альфе, и физическая приёмка ни разу не выполнялась — именно поэтому она и нужна. Ожидаемо, что что-то всплывёт: это её цель, а не побочный эффект.

Риск для 3x-ui я оцениваю как низкий: пересечений по портам, таблицам nftables, юнитам и конфигам нет, всё именовано префиксом проекта. Но «низкий» не значит «нулевой», и снапшот перед началом снимает вопрос полностью.

Если эти три сервера обслуживают что-то, чей простой недопустим, дешевле поднять три временных VPS на день. Приёмке всё равно, какие это машины.
