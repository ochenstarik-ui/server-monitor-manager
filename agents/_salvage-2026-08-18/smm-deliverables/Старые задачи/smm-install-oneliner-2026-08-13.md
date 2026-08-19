# Установка на серверы — одной командой

Работает **после выпуска `v0.1.0-alpha.14`**. Сейчас опубликован `alpha.13`, а в нём подпись без сертификата, поэтому проверка на нём откажет. Выпуск alpha.14 — задание Hermes.

Проверить, что релиз вышел:

```bash
curl -fsSI https://github.com/ochenstarik-ui/server-monitor-manager/releases/download/v0.1.0-alpha.14/smm-setup.sh | head -1
```

Ответ `HTTP/2 200` — можно ставить. `404` — релиз ещё не опубликован.

---

## Роли

Нужны четыре машины: Hub плюс три Node. Hub и Node нельзя совмещать — оба пишут `/etc/wireguard/smm0.conf`.

| Машина | Роль | Требования |
|---|---|---|
| A, публичный IP | Control Hub | свободны TCP 7443, UDP 51820 |
| B | Node `ai-agent` | только исходящий доступ |
| C | Node `home` | только исходящий доступ |
| D | Node `second` | только исходящий доступ, годится домашняя ВМ |

Ниже всё для `x86_64`. Для `arm64` замените `linux-x64` на `linux-arm64`.

Снимите снапшоты у провайдера до начала.

---

## Hub — одна команда

Подставьте публичное имя сервера A вместо `ВАШ-ХОСТ`:

```bash
TAG=v0.1.0-alpha.14 ARCH=linux-x64 HOST=ВАШ-ХОСТ; \
B=https://github.com/ochenstarik-ui/server-monitor-manager/releases/download/$TAG; \
sudo apt-get update -qq && sudo apt-get install -y -qq --no-install-recommends curl ca-certificates openssl coreutils tar netcat-openbsd && \
mkdir -p ~/smm && cd ~/smm && \
for f in smm-setup.sh smm-setup.sh.sha256 server-monitor-manager-$ARCH.tar.gz server-monitor-manager-$ARCH.tar.gz.sha256 server-monitor-manager-manifest.json server-monitor-manager-manifest.sig server-monitor-manager-manifest.pem; do curl -fsSL -O "$B/$f"; done && \
sha256sum -c smm-setup.sh.sha256 && sha256sum -c server-monitor-manager-$ARCH.tar.gz.sha256 && \
sudo bash smm-setup.sh install-control server-monitor-manager-$ARCH.tar.gz "$HOST" 7443 && \
sudo bash smm-setup.sh mesh-init "$HOST" 51820
```

Команда скачивает установщик, архив и все три файла подписи, сверяет контрольные суммы, ставит Control и поднимает mesh. Проверка подписи manifest выполняется внутри `install-control` — если она не сойдётся, установка прервётся.

Затем откройте в панели хостера **TCP 7443** и **UDP 51820** и, если включён `ufw`:

```bash
sudo ufw allow 7443/tcp comment 'SMM Control' && sudo ufw allow 51820/udp comment 'SMM WireGuard'
```

Проверка:

```bash
sudo curl -sf --cacert /etc/ochenstarik-server-monitor-manager/control-ca.crt https://ВАШ-ХОСТ:7443/healthz && echo " — Control отвечает"
```

---

## Node — одна команда

На каждом из B, C, D. Сначала на Hub выпустите код **непосредственно перед установкой** — он живёт 10 минут:

```bash
sudo bash ~/smm/smm-setup.sh node-code ai-agent
```

Для C — `home`, для D — `second`. Скопируйте строку `SMMNODE2....`.

Затем на Node:

```bash
TAG=v0.1.0-alpha.14 ARCH=linux-x64; \
B=https://github.com/ochenstarik-ui/server-monitor-manager/releases/download/$TAG; \
sudo apt-get update -qq && sudo apt-get install -y -qq --no-install-recommends curl ca-certificates openssl coreutils tar netcat-openbsd && \
mkdir -p ~/smm && cd ~/smm && \
for f in smm-setup.sh smm-setup.sh.sha256 server-monitor-manager-$ARCH.tar.gz server-monitor-manager-$ARCH.tar.gz.sha256 server-monitor-manager-manifest.json server-monitor-manager-manifest.sig server-monitor-manager-manifest.pem; do curl -fsSL -O "$B/$f"; done && \
sha256sum -c smm-setup.sh.sha256 && sha256sum -c server-monitor-manager-$ARCH.tar.gz.sha256 && \
sudo bash smm-setup.sh install-node server-monitor-manager-$ARCH.tar.gz
```

Установщик спросит код — вставьте `SMMNODE2....`, ввод скрытый. Сверьте показанный отпечаток CA с тем, что выдаёт Hub по команде `sudo bash ~/smm/smm-setup.sh control-ca-fingerprint`, и введите `yes`.

На выходе появится `SMMPEER1....` — скопируйте и активируйте на Hub:

```bash
sudo bash ~/smm/smm-setup.sh peer-add 'SMMPEER1....'
```

После всех трёх на Hub:

```bash
sudo bash ~/smm/smm-setup.sh mesh-status
```

Должны быть три строки `active` со свежими handshake.

---

## Приёмка

С вашей машины, из WSL или Git Bash, в клоне репозитория:

```bash
export HUB_SSH_HOST=A HUB_SSH_USER=пользователь
export SOURCE_SSH_HOST=B SOURCE_SSH_USER=пользователь
export SSH_IDENTITY_FILE="$HOME/.ssh/id_ed25519"
export SOURCE_NODE_ID=ai-agent HOME_NODE_ID=home SECOND_NODE_ID=second
export HOME_WG_IP=10.77.0.x SECOND_WG_IP=10.77.0.y
export TARGET_PORT=22
bash tests/acceptance/three-server-mesh.sh
```

Адреса возьмите из `mesh-status`. Этот прогон ничего не перезагружает. Успех — `THREE_SERVER_ACCEPTANCE=PASS`.

Полная приёмка добавляет перезагрузку Hub и источника — делать в низкий трафик:

```bash
SMM_ACCEPT_RESTORE=1 SMM_ACCEPT_REBOOT=1 bash tests/acceptance/three-server-mesh.sh
```

---

## Откат

```bash
sudo bash ~/smm/smm-setup.sh uninstall-agent --purge
sudo bash ~/smm/smm-setup.sh uninstall-control --confirm-destroy-control
sudo systemctl disable --now wg-quick@smm0 2>/dev/null || true
sudo rm -f /etc/wireguard/smm0.conf /etc/sysctl.d/90-ochenstarik-smm-mesh.conf
sudo nft delete table inet ochenstarik_smm 2>/dev/null || true
```

Аварийно отключить mesh, не трогая Control, Agent, SSH и 3x-ui:

```bash
sudo ochenstarik-smm-emergency mesh-disable
```
