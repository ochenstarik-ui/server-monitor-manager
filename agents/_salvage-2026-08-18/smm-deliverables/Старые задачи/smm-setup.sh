#!/usr/bin/env bash
# Установщик Server Monitor Manager для одного сервера.
#
#   sudo ./smm-setup.sh hub  <публичный-хост> [https-порт] [wg-порт]
#   sudo ./smm-setup.sh node
#   sudo ./smm-setup.sh check
#
# Скачивает bootstrap и release-архив из GitHub Release, проверяет контрольные
# суммы и выполняет установку выбранной роли. Ничего не удаляет.

set -Eeuo pipefail
IFS=$'\n\t'

REPO="${SMM_REPO:-ochenstarik-ui/server-monitor-manager}"
TAG="${SMM_TAG:-v0.1.0-alpha.8}"
WORK_DIR="${SMM_WORK_DIR:-/opt/smm}"
HTTPS_PORT_DEFAULT=7443
WG_PORT_DEFAULT=51820

say()  { printf '\n[smm-setup] %s\n' "$*"; }
ok()   { printf '[smm-setup]   ok: %s\n' "$*"; }
fail() { printf '\n[smm-setup] ОШИБКА: %s\n' "$*" >&2; exit 1; }

require_root() {
    [[ ${EUID:-$(id -u)} -eq 0 ]] || fail "запускать через sudo"
}

check_platform() {
    say "Проверка платформы"
    [[ -r /etc/os-release ]] || fail "/etc/os-release отсутствует"
    # shellcheck disable=SC1091
    . /etc/os-release
    case "${ID:-}" in
        ubuntu) case "${VERSION_ID:-}" in 22.04|24.04) ;; *) fail "Ubuntu ${VERSION_ID:-?} не поддерживается, нужна 22.04 или 24.04" ;; esac ;;
        debian) case "${VERSION_ID:-}" in 12|13) ;; *) fail "Debian ${VERSION_ID:-?} не поддерживается, нужна 12 или 13" ;; esac ;;
        *) fail "дистрибутив ${ID:-?} не поддерживается: нужны Ubuntu или Debian" ;;
    esac
    [[ "$(ps -p 1 -o comm=)" == "systemd" ]] || fail "PID 1 не systemd"
    case "$(uname -m)" in
        x86_64)         RUNTIME="linux-x64" ;;
        aarch64|arm64)  RUNTIME="linux-arm64" ;;
        *) fail "архитектура $(uname -m) не поддерживается" ;;
    esac
    ok "${ID} ${VERSION_ID}, $(uname -m) -> ${RUNTIME}"
}

# Возвращает 1, если что-то занято. Решение принимает вызывающий.
check_ports() {
    local role="$1" busy=0
    say "Проверка портов"
    if [[ "$role" == "hub" ]]; then
        if ss -lntu 2>/dev/null | grep -qE ":${HTTPS_PORT}\b"; then
            printf '[smm-setup]   ЗАНЯТ tcp/%s:\n' "$HTTPS_PORT"
            ss -lntup 2>/dev/null | grep -E ":${HTTPS_PORT}\b" || true
            busy=1
        else ok "tcp/${HTTPS_PORT} свободен"; fi
        if ss -lnu 2>/dev/null | grep -qE ":${WG_PORT}\b"; then
            printf '[smm-setup]   ЗАНЯТ udp/%s:\n' "$WG_PORT"
            ss -lnup 2>/dev/null | grep -E ":${WG_PORT}\b" || true
            busy=1
        else ok "udp/${WG_PORT} свободен"; fi
    fi
    if ip -4 route 2>/dev/null | grep -q '10\.77\.0\.'; then
        printf '[smm-setup]   ЗАНЯТА подсеть 10.77.0.0/24:\n'
        ip -4 route 2>/dev/null | grep '10\.77\.0\.' || true
        busy=1
    else ok "подсеть 10.77.0.0/24 свободна"; fi
    return "$busy"
}

show_state() {
    # Все проверки здесь диагностические: ненулевой код — это нормальный ответ,
    # а не отказ. Поэтому каждая обёрнута так, чтобы не уронить set -e.
    say "Текущее состояние"
    local units xui
    units="$(systemctl is-active \
        ochenstarik-smm-control.service \
        ochenstarik-smm-agent.service \
        ochenstarik-smm-provisioning-helper.service \
        wg-quick@smm0.service 2>&1 | paste -sd' ' || true)"
    printf '[smm-setup]   юниты: %s\n' "${units:-неизвестно}"
    if [[ -e /etc/wireguard/smm0.conf ]]; then ok "smm0 настроен"; else ok "smm0 не настроен"; fi
    if nft list table inet ochenstarik_smm >/dev/null 2>&1; then ok "таблица nftables есть"; else ok "таблицы nftables нет"; fi
    xui="$(systemctl is-active x-ui 2>/dev/null || true)"
    printf '[smm-setup]   x-ui: %s\n' "${xui:-не установлен}"
}

install_dependencies() {
    say "Установка зависимостей"
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -qq
    apt-get install -y -qq --no-install-recommends \
        curl ca-certificates openssl coreutils tar sudo util-linux netcat-openbsd
    ok "зависимости установлены"
}

download_and_verify() {
    local base="https://github.com/${REPO}/releases/download/${TAG}"
    local archive="server-monitor-manager-${RUNTIME}.tar.gz"
    say "Загрузка релиза ${TAG}"
    install -d -m 0755 "$WORK_DIR"
    cd "$WORK_DIR"
    printf '[smm-setup]   архив ~24 МБ, это может занять минуту\n'
    local file
    for file in \
        "ochenstarik-server-monitor-manager.sh" \
        "ochenstarik-server-monitor-manager.sh.sha256" \
        "$archive" \
        "${archive}.sha256"
    do
        printf '[smm-setup]   качаю %s\n' "$file"
        curl -fL --retry 3 --connect-timeout 20 --progress-bar -o "$file" "${base}/${file}" \
            || fail "не удалось скачать ${file} из релиза ${TAG}. Тег опубликован? Артефакты собраны?"
        ok "получен $file ($(du -h "$file" | cut -f1))"
    done

    say "Проверка контрольных сумм"
    sha256sum -c ochenstarik-server-monitor-manager.sh.sha256 || fail "контрольная сумма bootstrap не совпала"
    sha256sum -c "${archive}.sha256" || fail "контрольная сумма архива не совпала"
    ok "суммы совпали"

    chmod 700 ochenstarik-server-monitor-manager.sh



    ARCHIVE_PATH="${WORK_DIR}/${archive}"
    BOOTSTRAP="${WORK_DIR}/ochenstarik-server-monitor-manager.sh"
}

run_preflight() {
    say "Preflight"
    "$BOOTSTRAP" preflight
    "$BOOTSTRAP" verify-release "$ARCHIVE_PATH"
    ok "релиз пригоден"
}

install_hub() {
    say "Установка Control Hub"
    "$BOOTSTRAP" install-control "$ARCHIVE_PATH" "$PUBLIC_HOST" "$HTTPS_PORT"
    say "Инициализация mesh"
    "$BOOTSTRAP" mesh-init "$PUBLIC_HOST" "$WG_PORT"
    say "Состояние"
    "$BOOTSTRAP" status
    "$BOOTSTRAP" control-ca-fingerprint

    cat <<EOF

[smm-setup] ГОТОВО. Hub установлен.

Откройте во внешнем firewall хостера:
    TCP  ${HTTPS_PORT}
    UDP  ${WG_PORT}

Дальше для каждого Node на этом Hub выпустите код:
    sudo ${BOOTSTRAP} node-code <имя-node>

Имена для приёмки: ai-agent, home, second.
Полученный SMMNODE2-код введите на Node командой:
    sudo ./smm-setup.sh node

Возвращённый Node код SMMPEER1 активируйте здесь:
    sudo ${BOOTSTRAP} peer-add 'SMMPEER1....'
    sudo ${BOOTSTRAP} mesh-status
EOF
}

install_node() {
    say "Установка Node"
    cat <<'EOF'
[smm-setup] Сейчас будет запрошен одноразовый код SMMNODE2, выданный на Hub
[smm-setup] командой node-code. После него сверьте отпечаток Control CA
[smm-setup] с тем, что показал Hub, и введите yes.
EOF
    "$BOOTSTRAP" install-node "$ARCHIVE_PATH"

    cat <<EOF

[smm-setup] ГОТОВО. Node установлен.

Скопируйте выведенный выше код SMMPEER1 и активируйте его на Hub:
    sudo /opt/smm/ochenstarik-server-monitor-manager.sh peer-add 'SMMPEER1....'

Проверка на этом сервере:
    sudo systemctl is-active ochenstarik-smm-agent.service
    sudo wg show smm0
EOF
}

usage() {
    cat <<EOF
Установщик Server Monitor Manager

  sudo ./smm-setup.sh hub <публичный-хост> [https-порт] [wg-порт]
  sudo ./smm-setup.sh node
  sudo ./smm-setup.sh check

Переменные окружения:
  SMM_TAG       тег релиза (сейчас ${TAG})
  SMM_REPO      репозиторий (сейчас ${REPO})
  SMM_WORK_DIR  рабочий каталог (сейчас ${WORK_DIR})
EOF
}

main() {
    local role="${1:-help}"
    case "$role" in
        hub)
            [[ $# -ge 2 ]] || fail "укажите публичный хост: sudo ./smm-setup.sh hub <хост> [порт] [wg-порт]"
            PUBLIC_HOST="$2"
            HTTPS_PORT="${3:-$HTTPS_PORT_DEFAULT}"
            WG_PORT="${4:-$WG_PORT_DEFAULT}"
            [[ "$PUBLIC_HOST" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$ ]] \
                || fail "некорректный публичный хост: $PUBLIC_HOST"
            require_root; check_platform; show_state
            check_ports hub || fail "освободите занятое либо задайте другие порты аргументами"
            install_dependencies; download_and_verify; run_preflight; install_hub
            ;;
        node)
            HTTPS_PORT="$HTTPS_PORT_DEFAULT"; WG_PORT="$WG_PORT_DEFAULT"
            require_root; check_platform; show_state
            check_ports node || fail "освободите подсеть 10.77.0.0/24"
            install_dependencies; download_and_verify; run_preflight; install_node
            ;;
        check)
            HTTPS_PORT="${2:-$HTTPS_PORT_DEFAULT}"; WG_PORT="${3:-$WG_PORT_DEFAULT}"
            require_root; check_platform; show_state
            check_ports hub || true
            say "Диагностика завершена, изменений не вносилось"
            ;;
        help|-h|--help) usage ;;
        *) usage; fail "неизвестная роль: $role" ;;
    esac
}

main "$@"
