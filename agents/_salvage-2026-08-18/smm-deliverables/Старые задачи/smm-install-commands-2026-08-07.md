# Команды установки — копировать и выполнять

## Машины

| Роль | Что ставим | Требования |
|---|---|---|
| **A** — публичный IP | Control Hub | свободны TCP 7443, UDP 51820 |
| **B** | Node `ai-agent` | только исходящий доступ к A |
| **C** | Node `home` | только исходящий доступ к A |
| **D** | Node `second` | только исходящий доступ к A |

D может быть виртуалкой дома за NAT — входящие порты Node не нужны. Если четвёртой машины нет, приёмка пройдёт частично: два независимых назначения нужны, чтобы проверить, что отключение одного Link не рвёт второй.

Ниже всё для `x86_64`. Для `arm64` замените `linux-x64` на `linux-arm64` во всех командах.

---

## Шаг 0. На вашей Windows-машине: собрать артефакты

Опубликованный релиз `v0.1.0-alpha.6` устарел — в нём нет реконсиляции Links, и приёмка на нём проверит не то. Собираем из текущего `main`.

```powershell
gh workflow run "Linux release artifacts" --repo ochenstarik-ui/server-monitor-manager --ref main
```

Подождать ~3 минуты, затем:

```powershell
gh run list --repo ochenstarik-ui/server-monitor-manager --workflow "Linux release artifacts" --limit 1
```

Взять ID из вывода и скачать:

```powershell
gh run download <ID> --repo ochenstarik-ui/server-monitor-manager -D C:\Users\Ochenstarik\smm-artifacts
```

Получится три папки. Нужные файлы:

```
C:\Users\Ochenstarik\smm-artifacts\server-monitor-manager-bootstrap\ochenstarik-server-monitor-manager.sh
C:\Users\Ochenstarik\smm-artifacts\server-monitor-manager-bootstrap\ochenstarik-server-monitor-manager.sh.sha256
C:\Users\Ochenstarik\smm-artifacts\server-monitor-manager-linux-x64\server-monitor-manager-linux-x64.tar.gz
C:\Users\Ochenstarik\smm-artifacts\server-monitor-manager-linux-x64\server-monitor-manager-linux-x64.tar.gz.sha256
```

Скопировать на каждый из четырёх серверов:

```powershell
$files = @(
  "C:\Users\Ochenstarik\smm-artifacts\server-monitor-manager-bootstrap\ochenstarik-server-monitor-manager.sh",
  "C:\Users\Ochenstarik\smm-artifacts\server-monitor-manager-bootstrap\ochenstarik-server-monitor-manager.sh.sha256",
  "C:\Users\Ochenstarik\smm-artifacts\server-monitor-manager-linux-x64\server-monitor-manager-linux-x64.tar.gz",
  "C:\Users\Ochenstarik\smm-artifacts\server-monitor-manager-linux-x64\server-monitor-manager-linux-x64.tar.gz.sha256"
)
foreach ($host_ in @("<A>","<B>","<C>","<D>")) {
  ssh <пользователь>@$host_ "mkdir -p ~/smm"
  scp $files <пользователь>@${host_}:~/smm/
}
```

---

## Шаг 1. Зависимости и предпроверка — на каждом из четырёх серверов

Поддерживаются **Ubuntu 22.04 и 24.04**, Debian 12 и 13. На другой системе `preflight` откажет.

Установить зависимости:

```bash
sudo apt-get update
sudo apt-get install -y --no-install-recommends \
  openssl coreutils tar sudo util-linux ca-certificates
```

**Дополнительно на сервере B** (Node `ai-agent`, источник) — без него приёмка упадёт на последнем шаге:

```bash
sudo apt-get install -y --no-install-recommends netcat-openbsd
```

Проверка связности в приёмке выполняется командой `nc` **на самом источнике**, а скрипт этого не проверяет — он проверяет наличие `nc` только на вашей машине.

`wireguard-tools`, `nftables` и `iproute2` ставит сам bootstrap на шаге `mesh-init`, отдельно их устанавливать не нужно.

Предпроверка:

```bash
cd ~/smm
. /etc/os-release; echo "ОС: $ID $VERSION_ID $(uname -m)"; ps -p 1 -o comm=
echo "--- порты 7443 / 51820 ---"; ss -lntup | grep -E ':7443|:51820' || echo "свободны"
echo "--- таблицы nftables ---"; nft list tables 2>/dev/null || echo "nft не установлен"
echo "--- интерфейсы WireGuard ---"; wg show interfaces 2>/dev/null || echo "нет"
echo "--- подсеть 10.77.0.0/24 ---"; ip -4 route | grep '10\.77\.' || echo "свободна"
echo "--- 3x-ui / xray ---"; ss -lntup | grep -Ei 'x-ui|xray' || echo "не найдено"
```

Продолжать, только если ОС — Ubuntu 22.04/24.04 или Debian 12/13, PID 1 это `systemd`, порты свободны и подсеть не занята.

**Снимите снапшоты у провайдера перед следующим шагом.**

---

## Шаг 2. Сервер A — Control Hub

```bash
cd ~/smm
sha256sum -c ochenstarik-server-monitor-manager.sh.sha256
chmod 700 ochenstarik-server-monitor-manager.sh
sudo ./ochenstarik-server-monitor-manager.sh preflight
sudo ./ochenstarik-server-monitor-manager.sh verify-release ./server-monitor-manager-linux-x64.tar.gz
```

Замените `<A-хост>` на публичный IP или DNS-имя сервера A:

```bash
sudo ./ochenstarik-server-monitor-manager.sh install-control ./server-monitor-manager-linux-x64.tar.gz <A-хост> 7443
sudo ./ochenstarik-server-monitor-manager.sh mesh-init <A-хост> 51820
sudo ./ochenstarik-server-monitor-manager.sh status
```

Откройте во внешнем firewall провайдера **TCP 7443** и **UDP 51820**.

Проверьте, что 3x-ui работает, прежде чем идти дальше.

---

## Шаг 3. Регистрация Node — по одному

Для каждого Node цикл одинаковый: код выпускается на A, вводится на Node, обратный код возвращается на A.

### 3.1. На A — выпустить код для `ai-agent`

```bash
sudo ./ochenstarik-server-monitor-manager.sh node-code ai-agent
sudo ./ochenstarik-server-monitor-manager.sh control-ca-fingerprint
```

Скопируйте строку `SMMNODE2....` и отпечаток CA.

### 3.2. На B — установить Node

```bash
cd ~/smm
sha256sum -c ochenstarik-server-monitor-manager.sh.sha256
chmod 700 ochenstarik-server-monitor-manager.sh
sudo ./ochenstarik-server-monitor-manager.sh preflight
sudo ./ochenstarik-server-monitor-manager.sh install-node ./server-monitor-manager-linux-x64.tar.gz
```

Вставьте `SMMNODE2....` в скрытый запрос. Сверьте показанный отпечаток CA с тем, что выдал A, и введите `yes`.

На выходе будет строка `SMMPEER1....` — скопируйте её.

### 3.3. На A — активировать peer

```bash
sudo ./ochenstarik-server-monitor-manager.sh peer-add 'SMMPEER1....'
sudo ./ochenstarik-server-monitor-manager.sh mesh-status
```

### 3.4. Повторить 3.1–3.3 для C и D

Для C — `node-code home`, для D — `node-code second`.

После всех трёх на A:

```bash
sudo ./ochenstarik-server-monitor-manager.sh mesh-status
```

Должны быть три строки со статусом `active` и свежими handshake. Запишите адреса `home` и `second` — они понадобятся дальше.

---

## Шаг 4. Проверка перед приёмкой

На A:

```bash
sudo systemctl is-active ochenstarik-smm-control.service
sudo curl --fail --silent --cacert /etc/ochenstarik-server-monitor-manager/control-ca.crt https://127.0.0.1:7443/healthz && echo " Control OK"
```

На B, C, D:

```bash
sudo systemctl is-active ochenstarik-smm-agent.service
sudo systemctl is-active ochenstarik-smm-provisioning-helper.service
sudo wg show smm0
```

На каждом — убедитесь, что 3x-ui и Xray живы:

```bash
sudo systemctl is-active x-ui 2>/dev/null || sudo systemctl list-units --type=service | grep -i 'x-ui\|xray'
```

---

## Шаг 5. Приёмка — с вашей машины

Запускать из WSL (Ubuntu) или Git Bash. В WSL поставить зависимости:

```bash
sudo apt-get update
sudo apt-get install -y --no-install-recommends curl jq openssl openssh-client netcat-openbsd coreutils
```

```bash
cd /путь/к/клону/server-monitor-manager

export HUB_SSH_HOST=<A>
export HUB_SSH_USER=<пользователь>
export SOURCE_SSH_HOST=<B>
export SOURCE_SSH_USER=<пользователь>
export SSH_IDENTITY_FILE="$HOME/.ssh/id_ed25519"
export SOURCE_NODE_ID=ai-agent
export HOME_NODE_ID=home
export SECOND_NODE_ID=second
export HOME_WG_IP=10.77.0.<из mesh-status>
export SECOND_WG_IP=10.77.0.<из mesh-status>
export TARGET_PORT=22

bash tests/acceptance/three-server-mesh.sh
```

Этот прогон **ничего не перезагружает**. Успех — строка `THREE_SERVER_ACCEPTANCE=PASS`.

Полная приёмка добавляет перезагрузку A и B — делать в низкий трафик:

```bash
SMM_ACCEPT_RESTORE=1 SMM_ACCEPT_REBOOT=1 bash tests/acceptance/three-server-mesh.sh
```

---

## Откат, если что-то пошло не так

На Node:

```bash
sudo ochenstarik-server-monitor-manager.sh uninstall-agent --purge
```

На Hub:

```bash
sudo ochenstarik-server-monitor-manager.sh uninstall-control --confirm-destroy-control
```

На любом сервере — полная зачистка следов mesh:

```bash
sudo systemctl disable --now wg-quick@smm0 2>/dev/null || true
sudo rm -f /etc/wireguard/smm0.conf /etc/sysctl.d/90-ochenstarik-smm-mesh.conf
sudo nft delete table inet ochenstarik_smm 2>/dev/null || true
sudo sysctl --system
```

Аварийно отключить mesh, не трогая Control, Agent, SSH и 3x-ui:

```bash
sudo ochenstarik-smm-emergency status
sudo ochenstarik-smm-emergency mesh-disable
```
