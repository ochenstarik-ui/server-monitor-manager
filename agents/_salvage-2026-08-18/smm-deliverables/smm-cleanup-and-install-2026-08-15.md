# Очистка и установка — готовые блоки

## 1. Очистка · выполнить на каждом сервере

Безопасно на машине, где SMM никогда не стояло: всё пропускается молча. Порядок важен — сначала остановка служб, потом удаление юнитов, иначе процесс осиротеет и продолжит держать порт.

```bash
sudo systemctl disable --now \
  ochenstarik-smm-control.service \
  ochenstarik-smm-agent.service \
  ochenstarik-smm-provisioning-helper.service \
  ochenstarik-smm-firewall.service \
  wg-quick@smm0.service 2>/dev/null || true
for u in ochenstarik-smm-control ochenstarik-smm-agent ochenstarik-monitor; do
  sudo pkill -u "$u" 2>/dev/null || true
done
sleep 2
for u in ochenstarik-smm-control ochenstarik-smm-agent ochenstarik-monitor; do
  sudo pkill -9 -u "$u" 2>/dev/null || true
done
sudo rm -f /etc/systemd/system/ochenstarik-smm-*.service
sudo rm -rf /usr/local/lib/ochenstarik-server-monitor-manager
sudo rm -f /usr/local/libexec/ochenstarik-smm-policy-apply /usr/local/libexec/ochenstarik-smm-metrics
sudo rm -f /usr/local/sbin/ochenstarik-smm-emergency /usr/local/sbin/ochenstarik-server-monitor-manager.sh
sudo rm -f /etc/sudoers.d/ochenstarik-smm-control
sudo rm -rf /var/lib/ochenstarik-server-monitor-manager \
            /var/lib/ochenstarik-server-monitor-manager-enrollment \
            /var/lib/ochenstarik-monitor \
            /etc/ochenstarik-server-monitor-manager \
            /opt/smm
sudo rm -f /etc/wireguard/smm0.conf /etc/sysctl.d/90-ochenstarik-smm-mesh.conf
sudo nft delete table inet ochenstarik_smm 2>/dev/null || true
for u in ochenstarik-smm-control ochenstarik-smm-agent ochenstarik-monitor; do
  sudo userdel "$u" 2>/dev/null || true
  sudo groupdel "$u" 2>/dev/null || true
done
sudo systemctl daemon-reload
sudo systemctl reset-failed 2>/dev/null || true
sudo sysctl --system >/dev/null
```

Проверка — все четыре строки должны сообщить, что пусто:

```bash
ss -lntu | grep -E ':7443|:51820' || echo "порты свободны"
ip -4 route | grep '10\.77\.0\.' || echo "подсеть свободна"
ls /etc/systemd/system/ochenstarik-smm-*.service 2>/dev/null || echo "юнитов нет"
getent passwd ochenstarik-smm-control >/dev/null && echo "пользователь остался" || echo "пользователей нет"
```

Если порт всё ещё занят — процесс осиротел, найдите и снимите:

```bash
sudo ss -lntup | grep -E ':7443|:51820'
sudo kill -9 <PID>
```

---

## 2. Зависимости · на каждом сервере

```bash
sudo apt-get update && sudo apt-get install -y --no-install-recommends \
  curl ca-certificates openssl coreutils tar netcat-openbsd
```

## 2b. cosign · на каждом сервере · временный шаг

Установщик проверяет подпись manifest через `cosign`, но `alpha.14` его не поставляет, а в репозиториях Ubuntu его нет. Без этого шага установка обрывается на строке `ERROR: Required command is missing: cosign`.

Для `arm64` замените `amd64` на `arm64` в обеих строках.

```bash
cd /tmp && \
COSIGN_VER=v3.1.3 && \
curl -fsSLO https://github.com/sigstore/cosign/releases/download/$COSIGN_VER/cosign-linux-amd64 && \
curl -fsSLO https://github.com/sigstore/cosign/releases/download/$COSIGN_VER/cosign_checksums.txt && \
grep ' cosign-linux-amd64$' cosign_checksums.txt | sha256sum -c - && \
sudo install -m 0755 cosign-linux-amd64 /usr/local/bin/cosign && \
cosign version
```

Ставится в `/usr/local/bin`, потому что этот каталог входит в `secure_path` sudo — иначе установщик всё равно не увидит бинарь.

Шаг исчезнет в `v0.1.0-alpha.15`: cosign будет обеспечивать сам установщик.

---

## 3. Установка Hub · только на сервере с публичным IP

Путь проверен живой установкой 2026-08-15 на Ubuntu 24.04 x86_64. Команду `smm-setup.sh install-hub` **не используем**: она качает 24 МБ молча, а разбор аргументов делает уже после скачивания. Ниже — по шагам, с видимым прогрессом; заодно окно SSH не засыпает.

Для `arm64` замените `linux-x64` на `linux-arm64` во всех строках.

**3.1. Установщик**

```bash
mkdir -p ~/smm && cd ~/smm && B=https://github.com/ochenstarik-ui/server-monitor-manager/releases/download/v0.1.0-alpha.14 && curl -fsSL -O "$B/smm-setup.sh" -O "$B/smm-setup.sh.sha256" && sha256sum -c smm-setup.sh.sha256
```

**3.2. Пять релизных файлов.** Manifest, подпись и сертификат обязаны лежать **рядом с архивом** — установщик ищет их в каталоге архива

```bash
cd ~/smm && B=https://github.com/ochenstarik-ui/server-monitor-manager/releases/download/v0.1.0-alpha.14 && for f in server-monitor-manager-linux-x64.tar.gz server-monitor-manager-linux-x64.tar.gz.sha256 server-monitor-manager-manifest.json server-monitor-manager-manifest.sig server-monitor-manager-manifest.pem; do printf '>>> %s\n' "$f"; curl -fL --progress-bar -o "$f" "$B/$f"; done && sha256sum -c server-monitor-manager-linux-x64.tar.gz.sha256
```

**3.3. Проверка подписи отдельным шагом** — быстрая, и сразу видно, работает ли cosign

```bash
cd ~/smm && bash smm-setup.sh verify-manifest server-monitor-manager-manifest.json server-monitor-manager-manifest.sig server-monitor-manager-manifest.pem
```

Ожидается `Manifest signature is valid`. Если нет — дальше не идти.

**3.4. Control.** Вместо `ВАШ-ХОСТ` — публичный IP или DNS-имя

```bash
cd ~/smm && bash smm-setup.sh install-control server-monitor-manager-linux-x64.tar.gz ВАШ-ХОСТ 7443
```

**3.5. Mesh**

```bash
cd ~/smm && bash smm-setup.sh mesh-init ВАШ-ХОСТ 51820
```

Шаг 3.4 выведет отпечаток CA — запишите, он понадобится при регистрации каждого Node. Повторно его можно получить так:

```bash
bash ~/smm/smm-setup.sh control-ca-fingerprint
```

### Установленный Hub

- `178.212.13.102` (`host1885995-3.hostland.pro`), Ubuntu 24.04 x86_64, поставлен 2026-08-15 из `v0.1.0-alpha.14`;
- отпечаток CA: `EB:05:E5:16:42:EE:97:28:C2:E9:EA:6F:3E:48:3C:0C:1C:1E:02:E0:ED:9B:AF:7B:BF:24:2C:19:D4:4B:11:C4`;
- публичный ключ WireGuard: `AjN8XwPB11bRQa0fO5ljPlAWc0FxU+fxbeUT8AZczHk=`;
- mesh: `10.77.0.1/24` на `smm0`.

Откройте у хостера **TCP 7443** и **UDP 51820**, и если включён `ufw`:

```bash
sudo ufw allow 7443/tcp comment 'SMM Control' && sudo ufw allow 51820/udp comment 'SMM WireGuard'
```

Проверка:

```bash
sudo curl -sf --cacert /etc/ochenstarik-server-monitor-manager/control-ca.crt \
  https://ВАШ-ХОСТ:7443/healthz && echo " — Control отвечает"
```

---

## 4. Установка Node · на каждом из трёх остальных серверов

**Сначала на Hub** выпустите код — он живёт 10 минут, поэтому делайте это непосредственно перед установкой:

```bash
sudo bash ~/smm/smm-setup.sh node-code ai-agent
```

Имена по одному на сервер: `ai-agent`, `home`, `second`. Скопируйте строку `SMMNODE2....`.

**Затем на Node** — шаги 1, 2b и 3.1–3.3 из этого документа, затем:

```bash
cd ~/smm && sudo bash smm-setup.sh -- install-node server-monitor-manager-linux-x64.tar.gz
```

Разделитель `--` обязателен. У обёртки `smm-setup.sh` есть собственная команда `install-node` **без аргументов**, которая качает всё заново молча; `--` заставляет передать вызов дальше, в bootstrap, где `install-node` принимает путь к уже скачанному архиву. Без `--` получите `install-node takes no arguments`.

Установщик запросит код — вставьте `SMMNODE2....`, ввод скрытый. Сверьте показанный отпечаток CA с тем, что выдал Hub, введите `yes`.

На выходе появится `SMMPEER1....` — скопируйте и активируйте **на Hub**:

```bash
sudo bash ~/smm/smm-setup.sh peer-add 'SMMPEER1....'
```

После всех трёх, на Hub:

```bash
sudo bash ~/smm/smm-setup.sh mesh-status
```

Три строки со статусом `active` и свежими handshake — mesh собран.

---

## Порядок целиком

1. Очистка на всех четырёх машинах.
2. Зависимости на всех четырёх.
3. Hub на сервере с публичным IP, открыть порты.
4. Node на трёх остальных, по одному: код на Hub → установка на Node → `peer-add` на Hub.
5. `mesh-status` — три `active`.
6. Приёмка с вашей машины.

Установка не начнётся, пока не выпущен `v0.1.0-alpha.14`. Проверка:

```bash
curl -fsSI https://github.com/ochenstarik-ui/server-monitor-manager/releases/download/v0.1.0-alpha.14/smm-setup.sh | head -1
```
