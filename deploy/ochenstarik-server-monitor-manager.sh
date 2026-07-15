#!/usr/bin/env bash
set -Eeuo pipefail

readonly APP_NAME="ochenstarik-server-monitor-manager"
readonly MONITOR_USER="ochenstarik-monitor"
readonly MONITOR_HOME="/var/lib/${APP_NAME}"
readonly MONITOR_COMMAND="/usr/local/libexec/ochenstarik-server-monitor"
readonly AUTHORIZED_KEYS="${MONITOR_HOME}/.ssh/authorized_keys"

log() { printf '[+] %s\n' "$*"; }
warn() { printf '[!] %s\n' "$*" >&2; }
die() { printf '[x] %s\n' "$*" >&2; exit 1; }

require_root() {
  [[ "$EUID" -eq 0 ]] || die "Запустите скрипт через sudo"
}

detect_system() {
  [[ -r /etc/os-release ]] || die "Не найден /etc/os-release"
  # shellcheck disable=SC1091
  . /etc/os-release
  case "${ID:-}" in
    ubuntu|debian) ;;
    *) die "Поддерживаются Ubuntu и Debian; обнаружено: ${ID:-unknown}" ;;
  esac
  command -v systemctl >/dev/null 2>&1 || die "Требуется systemd"
}

read_public_key() {
  if [[ -n "${SERVER_MONITOR_PUBLIC_KEY:-}" ]]; then
    PUBLIC_KEY="$SERVER_MONITOR_PUBLIC_KEY"
    log "Публичный ключ получен из SERVER_MONITOR_PUBLIC_KEY"
  else
    printf '\nВ Windows-приложении нажмите «SSH-ключ» → «Копировать».\n'
    if [[ -r /dev/tty ]]; then
      IFS= read -r -p 'Вставьте публичный ключ: ' PUBLIC_KEY < /dev/tty
    else
      die "Нет интерактивного терминала. Передайте ключ через SERVER_MONITOR_PUBLIC_KEY"
    fi
  fi
  PUBLIC_KEY="${PUBLIC_KEY//$'\r'/}"
  [[ "$PUBLIC_KEY" =~ ^ssh-ed25519[[:space:]]+[A-Za-z0-9+/]+={0,3}([[:space:]].*)?$ ]] \
    || die "Ожидается публичный ключ формата ssh-ed25519 AAAA..."
}

install_dependencies() {
  apt-get update
  DEBIAN_FRONTEND=noninteractive apt-get install -y \
    ca-certificates openssh-server openssh-client coreutils gawk
  systemctl enable --now ssh
}

verify_public_key() {
  local key_file
  key_file="$(mktemp)"
  trap 'rm -f -- "${key_file:-}"' RETURN
  printf '%s\n' "$PUBLIC_KEY" > "$key_file"
  ssh-keygen -l -f "$key_file" >/dev/null \
    || die "ssh-keygen отклонил публичный ключ"
  trap - RETURN
  rm -f -- "$key_file"
}

create_monitor_command() {
  install -d -m 0755 -o root -g root "$(dirname "$MONITOR_COMMAND")"
  [[ ! -L "$MONITOR_COMMAND" ]] \
    || die "Отказ от записи через символическую ссылку: $MONITOR_COMMAND"
  cat > "$MONITOR_COMMAND" <<'EOF'
#!/usr/bin/env bash
set -Eeuo pipefail
export LC_ALL=C

read_mem_value() {
  awk -v key="$1" '$1 == key ":" { print $2; exit }' /proc/meminfo
}

printf 'PROTOCOL=1\n'
printf 'HOSTNAME=%s\n' "$(hostname)"
printf 'UPTIME_SECONDS=%s\n' "$(cut -d. -f1 /proc/uptime)"
printf 'LOAD1=%s\n' "$(cut -d' ' -f1 /proc/loadavg)"
printf 'CPU_COUNT=%s\n' "$(getconf _NPROCESSORS_ONLN)"
printf 'MEM_TOTAL_KB=%s\n' "$(read_mem_value MemTotal)"
printf 'MEM_AVAILABLE_KB=%s\n' "$(read_mem_value MemAvailable)"
df -Pk / | awk 'NR == 2 {
  printf "DISK_TOTAL_KB=%s\nDISK_AVAILABLE_KB=%s\n", $2, $4
}'
printf 'KERNEL=%s\n' "$(uname -r)"
EOF
  chown root:root "$MONITOR_COMMAND"
  chmod 0755 "$MONITOR_COMMAND"
}

create_monitor_user() {
  if ! getent passwd "$MONITOR_USER" >/dev/null; then
    useradd \
      --system \
      --create-home \
      --home-dir "$MONITOR_HOME" \
      --shell /bin/bash \
      "$MONITOR_USER"
  fi
  usermod --home "$MONITOR_HOME" --shell /bin/bash "$MONITOR_USER"
  passwd -l "$MONITOR_USER" >/dev/null 2>&1 || true

  install -d -m 0750 -o "$MONITOR_USER" -g "$MONITOR_USER" "$MONITOR_HOME"
  install -d -m 0700 -o "$MONITOR_USER" -g "$MONITOR_USER" "${MONITOR_HOME}/.ssh"
  [[ ! -L "$AUTHORIZED_KEYS" ]] \
    || die "Отказ от записи через символическую ссылку: $AUTHORIZED_KEYS"
  printf 'restrict,command="%s" %s\n' "$MONITOR_COMMAND" "$PUBLIC_KEY" > "$AUTHORIZED_KEYS"
  chown "$MONITOR_USER:$MONITOR_USER" "$AUTHORIZED_KEYS"
  chmod 0600 "$AUTHORIZED_KEYS"
}

verify_sshd() {
  sshd -t
  sshd -T | grep -qi '^pubkeyauthentication yes$' \
    || die "В sshd отключена аутентификация по публичному ключу"
}

show_status() {
  local ssh_port="unknown"
  if command -v sshd >/dev/null 2>&1; then
    ssh_port="$(sshd -T 2>/dev/null | awk '$1 == "port" { print $2; exit }')"
  fi
  printf 'Пользователь: %s\n' "$MONITOR_USER"
  printf 'SSH-порт: %s\n' "${ssh_port:-unknown}"
  printf 'Команда: %s\n' "$MONITOR_COMMAND"
  if [[ -x "$MONITOR_COMMAND" ]]; then
    runuser -u "$MONITOR_USER" -- "$MONITOR_COMMAND"
  else
    warn "Серверная часть ещё не установлена"
  fi
}

install_server_part() {
  read_public_key
  install_dependencies
  verify_public_key
  create_monitor_command
  create_monitor_user
  verify_sshd
  systemctl reload ssh
  log "Серверная часть установлена"
  log "Входящий порт не изменялся, новые правила UFW не создавались"
  show_status
}

uninstall_server_part() {
  local answer
  if [[ -r /dev/tty ]]; then
    IFS= read -r -p 'Удалить пользователя мониторинга и forced-command? [y/N]: ' answer < /dev/tty
  else
    die "Для удаления требуется интерактивный терминал"
  fi
  [[ "$answer" =~ ^[Yy]$ ]] || { log "Отменено"; return 0; }
  rm -f -- "$MONITOR_COMMAND"
  if getent passwd "$MONITOR_USER" >/dev/null; then
    userdel --remove "$MONITOR_USER" 2>/dev/null || userdel "$MONITOR_USER"
  fi
  log "Серверная часть удалена"
}

main() {
  local action="${1:-install}"
  require_root
  detect_system
  case "$action" in
    install) install_server_part ;;
    status) show_status ;;
    uninstall) uninstall_server_part ;;
    *) die "Использование: $0 [install|status|uninstall]" ;;
  esac
}

main "$@"
