#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

readonly PROGRAM="ochenstarik-server-monitor-manager"
readonly PROGRAM_VERSION="0.2.0-dev"
readonly ETC_DIR="/etc/ochenstarik-server-monitor-manager"
readonly LIB_DIR="/usr/local/lib/ochenstarik-server-monitor-manager"
readonly STATE_DIR="/var/lib/ochenstarik-server-monitor-manager"
readonly ENROLLMENT_DIR="${STATE_DIR}-enrollment"
readonly BACKUP_DIR="${STATE_DIR}/bootstrap-backups"
readonly CONTROL_USER="ochenstarik-smm-control"
readonly AGENT_USER="ochenstarik-smm-agent"
readonly CONTROL_UNIT="ochenstarik-smm-control.service"
readonly AGENT_UNIT="ochenstarik-smm-agent.service"
readonly PROVISIONING_HELPER_UNIT="ochenstarik-smm-provisioning-helper.service"
readonly POLICY_HELPER="/usr/local/libexec/ochenstarik-smm-policy-apply"
readonly EMERGENCY_COMMAND="/usr/local/sbin/ochenstarik-smm-emergency"
readonly BOOTSTRAP_COMMAND="/usr/local/sbin/ochenstarik-server-monitor-manager.sh"
readonly SUDOERS_FILE="/etc/sudoers.d/ochenstarik-smm-control"
readonly MESH_DIR="${STATE_DIR}/mesh"
readonly WG_DIR="${ETC_DIR}/wireguard"
readonly FIREWALL_UNIT="ochenstarik-smm-firewall.service"
readonly MESH_NETWORK="10.77.0.0/24"
readonly HUB_MESH_ADDRESS="10.77.0.1/24"
readonly COSIGN_ISSUER="https://token.actions.githubusercontent.com"
readonly COSIGN_IDENTITY_REGEXP="^https://github.com/ochenstarik-ui/server-monitor-manager/\.github/workflows/linux-release\.yml@refs/tags/v.*$"

TEMP_DIR=""
MESH_PEER_CODE=""
ENROLLMENT_TOKEN_FILE=""
ENROLLMENT_TOKEN_TEMP=""
CONTROL_UPDATE_BACKUP_ID=""
CONTROL_UPDATE_RECOVERY_REQUIRED=0
CONTROL_UPDATE_LEGACY_ITEMS=()

log() { printf '%s\n' "[$PROGRAM] $*"; }
fail() { printf '%s\n' "[$PROGRAM] ERROR: $*" >&2; exit 1; }

cleanup() {
    local status=$?
    trap - EXIT
    if [[ "$CONTROL_UPDATE_RECOVERY_REQUIRED" == "1" ]]; then
        log "Control update failed; restoring the pre-update state."
        if recover_control_update "$CONTROL_UPDATE_BACKUP_ID"; then
            log "Control recovery completed."
        else
            printf '%s\n' "[$PROGRAM] ERROR: Automatic Control recovery failed; manual recovery is required." >&2
            status=1
        fi
    fi
    if [[ -n "$ENROLLMENT_TOKEN_FILE" ]]; then
        rm -f -- "$ENROLLMENT_TOKEN_FILE"
    fi
    if [[ -n "$ENROLLMENT_TOKEN_TEMP" ]]; then
        rm -f -- "$ENROLLMENT_TOKEN_TEMP"
    fi
    if [[ -n "$TEMP_DIR" && -d "$TEMP_DIR" ]]; then
        rm -rf -- "$TEMP_DIR"
    fi
    exit "$status"
}
trap cleanup EXIT

usage() {
    cat <<'EOF'
Server Monitor Manager Linux bootstrap

Usage:
  ochenstarik-server-monitor-manager.sh preflight
  ochenstarik-server-monitor-manager.sh verify-release ARCHIVE
  ochenstarik-server-monitor-manager.sh install-control ARCHIVE PUBLIC_HOST [HTTPS_PORT]
  ochenstarik-server-monitor-manager.sh install-agent ARCHIVE NODE_ID CONTROL_URL CA_CERT
  ochenstarik-server-monitor-manager.sh install-node ARCHIVE
  ochenstarik-server-monitor-manager.sh verify-manifest MANIFEST SIGNATURE
  ochenstarik-server-monitor-manager.sh mesh-init PUBLIC_ENDPOINT [WG_PORT]
  ochenstarik-server-monitor-manager.sh peer-add SMMPEER1_CODE
  ochenstarik-server-monitor-manager.sh mesh-status
  ochenstarik-server-monitor-manager.sh update-control ARCHIVE
  ochenstarik-server-monitor-manager.sh update-agent ARCHIVE
  ochenstarik-server-monitor-manager.sh rollback control|agent [BACKUP_ID]
  ochenstarik-server-monitor-manager.sh node-code NODE_ID
  ochenstarik-server-monitor-manager.sh control-device-code DEVICE_ID
  ochenstarik-server-monitor-manager.sh node-token NODE_ID
  ochenstarik-server-monitor-manager.sh control-ca-fingerprint
  ochenstarik-server-monitor-manager.sh status
  ochenstarik-server-monitor-manager.sh uninstall-agent [--purge]
  ochenstarik-server-monitor-manager.sh uninstall-control --confirm-destroy-control
  ochenstarik-server-monitor-manager.sh version

ARCHIVE must have a matching ARCHIVE.sha256 file. Agent enrollment reads the
one-time token from SMM_ENROLL_TOKEN or from a hidden local prompt; it is never
written to agent.env.
EOF
}

base64url_encode() {
    base64 -w 0 | tr '+/' '-_' | tr -d '='
}

base64url_decode() {
    local value="$1" remainder
    [[ "$value" =~ ^[A-Za-z0-9_-]+$ ]] || fail "Enrollment code contains invalid base64url data."
    remainder=$(( ${#value} % 4 ))
    case "$remainder" in
        0) ;;
        2) value+="==" ;;
        3) value+="=" ;;
        *) fail "Enrollment code contains invalid base64url length." ;;
    esac
    printf '%s' "$value" | tr '_-' '/+' | base64 -d
}

require_root() {
    [[ ${EUID:-$(id -u)} -eq 0 ]] || fail "This action must run as root (use sudo)."
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || fail "Required command is missing: $1"
}

validate_platform() {
    [[ -r /etc/os-release ]] || fail "/etc/os-release is missing."
    # shellcheck disable=SC1091
    . /etc/os-release
    case "${ID:-}" in
        ubuntu)
            case "${VERSION_ID:-}" in 22.04|24.04) ;; *) fail "Unsupported Ubuntu version: ${VERSION_ID:-unknown}" ;; esac
            ;;
        debian)
            case "${VERSION_ID:-}" in 12|13) ;; *) fail "Unsupported Debian version: ${VERSION_ID:-unknown}" ;; esac
            ;;
        *) fail "Unsupported distribution: ${ID:-unknown}" ;;
    esac
    case "$(uname -m)" in
        x86_64|aarch64|arm64) ;;
        *) fail "Unsupported architecture: $(uname -m)" ;;
    esac
    [[ "$(ps -p 1 -o comm=)" == "systemd" ]] || fail "systemd must be PID 1."
}

validate_node_id() {
    [[ "$1" =~ ^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$ ]] \
        || fail "Node id must contain 1-63 lowercase letters, digits, or hyphens."
}

validate_port() {
    [[ "$1" =~ ^[0-9]{1,5}$ ]] && (( 10#$1 >= 1 && 10#$1 <= 65535 )) \
        || fail "Port must be in range 1-65535."
}

validate_ipv4_literal() {
    local address="$1" octet
    local -a octets
    IFS=. read -r -a octets <<<"$address"
    (( ${#octets[@]} == 4 )) || fail "Control URL contains an invalid IPv4 host."
    for octet in "${octets[@]}"; do
        [[ "$octet" =~ ^[0-9]{1,3}$ ]] && (( 10#$octet <= 255 )) \
            || fail "Control URL contains an invalid IPv4 host."
    done
}

validate_control_url() {
    local authority host port="" label left right compressed=0 ipv4_groups=0 group_count last_group_index
    local -a groups labels right_groups
    [[ "$1" == https://* ]] || fail "Control URL must be an https URL without a path or credentials."
    authority="${1#https://}"
    authority="${authority%/}"
    [[ -n "$authority" && "$authority" != *['/?#@']* ]] \
        || fail "Control URL must be an https URL without a path or credentials."

    if [[ "$authority" == \[* ]]; then
        [[ "$authority" =~ ^\[([0-9A-Fa-f:.]+)\](:([0-9]+))?$ ]] \
            || fail "Control URL contains an invalid bracketed IPv6 authority."
        host="${BASH_REMATCH[1]}"
        port="${BASH_REMATCH[3]:-}"
        [[ "$host" == *:* ]] || fail "Control URL contains an invalid bracketed IPv6 authority."
        [[ "$host" != *:::* ]] || fail "Control URL contains an invalid bracketed IPv6 authority."
        [[ "$host" != :* || "$host" == ::* ]] \
            || fail "Control URL contains an invalid bracketed IPv6 authority."
        [[ "$host" != *: || "$host" == *:: ]] \
            || fail "Control URL contains an invalid bracketed IPv6 authority."
        groups=()
        if [[ "$host" == *::* ]]; then
            compressed=1
            [[ "${host/::/}" != *::* ]] \
                || fail "Control URL contains an invalid bracketed IPv6 authority."
            left="${host%%::*}"
            right="${host#*::}"
            if [[ -n "$left" ]]; then
                IFS=: read -r -a groups <<<"$left"
            fi
            if [[ -n "$right" ]]; then
                IFS=: read -r -a right_groups <<<"$right"
                groups+=("${right_groups[@]}")
            fi
        else
            IFS=: read -r -a groups <<<"$host"
        fi
        if (( ${#groups[@]} > 0 )) && [[ "${groups[${#groups[@]}-1]}" == *.* ]]; then
            last_group_index=$(( ${#groups[@]} - 1 ))
            validate_ipv4_literal "${groups[$last_group_index]}"
            unset "groups[$last_group_index]"
            ipv4_groups=2
        fi
        for label in "${groups[@]}"; do
            [[ "$label" =~ ^[0-9A-Fa-f]{1,4}$ ]] \
                || fail "Control URL contains an invalid bracketed IPv6 authority."
        done
        group_count=$(( ${#groups[@]} + ipv4_groups ))
        if (( compressed == 1 )); then
            (( group_count < 8 )) || fail "Control URL contains an invalid bracketed IPv6 authority."
        else
            (( group_count == 8 )) || fail "Control URL contains an invalid bracketed IPv6 authority."
        fi
    else
        [[ "$authority" != *:*:* ]] \
            || fail "Control URL IPv6 authorities must use balanced brackets."
        if [[ "$authority" == *:* ]]; then
            host="${authority%%:*}"
            port="${authority#*:}"
            [[ -n "$port" ]] || fail "Control URL contains an invalid port."
        else
            host="$authority"
        fi
        [[ "$host" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$ ]] \
            || fail "Control URL contains an invalid DNS or IPv4 host."
        [[ ${#host} -le 253 && "$host" != *..* ]] \
            || fail "Control URL contains an invalid DNS or IPv4 host."
        IFS=. read -r -a labels <<<"$host"
        for label in "${labels[@]}"; do
            [[ ${#label} -le 63 && "$label" =~ ^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?$ ]] \
                || fail "Control URL contains an invalid DNS or IPv4 host."
        done
        if [[ "$host" =~ ^[0-9.]+$ ]]; then
            validate_ipv4_literal "$host"
        fi
    fi
    [[ -n "$host" ]] || fail "Control URL host is empty."
    [[ -z "$port" ]] || validate_port "$port"
}

verify_manifest() {
    local manifest="$1" signature="$2"
    if [[ "${SMM_ALLOW_UNSIGNED:-0}" == "1" ]]; then
        log "WARNING: Signature verification skipped due to SMM_ALLOW_UNSIGNED=1."
        return 0
    fi
    require_command cosign
    [[ -f "$manifest" ]] || fail "Manifest not found: $manifest"
    [[ -f "$signature" ]] || fail "Signature not found: $signature"
    log "Verifying manifest signature..."
    if ! cosign verify-blob --certificate-oidc-issuer "$COSIGN_ISSUER" \
        --certificate-identity-regexp "$COSIGN_IDENTITY_REGEXP" \
        --signature "$signature" "$manifest" >/dev/null 2>&1; then
        fail "Manifest signature verification failed."
    fi
    log "Manifest signature is valid."
}

verify_archive() {
    local archive="$1" expected actual entry manifest signature
    [[ -f "$archive" ]] || fail "Archive not found: $archive"
    manifest="$(dirname "$archive")/server-monitor-manager-manifest.json"
    signature="$(dirname "$archive")/server-monitor-manager-manifest.sig"
    
    if [[ -f "$manifest" && -f "$signature" ]]; then
        verify_manifest "$manifest" "$signature"
        local archive_basename
        archive_basename="$(basename "$archive")"
        expected="$(awk -F'"' -v name="$archive_basename" '$2 == name {print $4}' "$manifest" || true)"
        [[ -n "$expected" ]] || fail "Could not extract archive hash from manifest."
    else
        if [[ "${SMM_ALLOW_UNSIGNED:-0}" == "1" ]]; then
            log "WARNING: Manifest and signature not found, falling back to .sha256 file due to SMM_ALLOW_UNSIGNED=1."
            local checksum_file="${archive}.sha256"
            [[ -f "$checksum_file" ]] || fail "Checksum file not found: $checksum_file"
            expected="$(awk 'NR == 1 { print $1 }' "$checksum_file")"
            [[ "$expected" =~ ^[0-9a-fA-F]{64}$ ]] || fail "Invalid checksum file: $checksum_file"
        else
            fail "Manifest and signature are required for archive verification. Set SMM_ALLOW_UNSIGNED=1 to bypass."
        fi
    fi

    actual="$(sha256sum "$archive" | awk '{ print $1 }')"
    [[ "${actual,,}" == "${expected,,}" ]] || fail "Archive checksum mismatch."

    while IFS= read -r entry; do
        [[ -n "$entry" ]] || continue
        [[ "$entry" != /* && "$entry" != *".."* ]] || fail "Unsafe archive entry: $entry"
        case "$entry" in
            agent|agent/*|control|control/*|provisioning-helper|provisioning-helper/*|deploy|deploy/*|bootstrap|bootstrap/*) ;;
            *) fail "Unexpected archive entry: $entry" ;;
        esac
    done < <(tar -tzf "$archive")
}

extract_archive() {
    local archive="$1"
    verify_archive "$archive"
    TEMP_DIR="$(mktemp -d -t smm-bootstrap.XXXXXXXX)"
    chmod 700 "$TEMP_DIR"
    tar -xzf "$archive" -C "$TEMP_DIR" --no-same-owner --no-same-permissions
    [[ -f "$TEMP_DIR/deploy/$CONTROL_UNIT" ]] || fail "Control systemd unit is missing from archive."
    [[ -f "$TEMP_DIR/deploy/$AGENT_UNIT" ]] || fail "Agent systemd unit is missing from archive."
    [[ -f "$TEMP_DIR/deploy/$PROVISIONING_HELPER_UNIT" ]] || fail "Provisioning helper systemd unit is missing from archive."
    [[ -f "$TEMP_DIR/deploy/$FIREWALL_UNIT" ]] || fail "Mesh firewall systemd unit is missing from archive."
    [[ -x "$TEMP_DIR/deploy/ochenstarik-smm-policy-apply" ]] || fail "Policy helper is missing from archive."
    [[ -x "$TEMP_DIR/deploy/ochenstarik-smm-emergency" ]] || fail "Emergency command is missing from archive."
}

verify_release_payload() {
    local archive="$1"
    require_command sha256sum
    require_command tar
    extract_archive "$archive"
    [[ -x "$TEMP_DIR/control/ochenstarik-smm-control" ]] || fail "Control binary is missing."
    [[ -x "$TEMP_DIR/agent/ochenstarik-smm-agent" ]] || fail "Agent binary is missing."
    [[ -x "$TEMP_DIR/provisioning-helper/ochenstarik-smm-provisioning-helper" ]] || fail "Provisioning helper binary is missing."
    [[ -x "$TEMP_DIR/deploy/ochenstarik-smm-policy-apply" ]] || fail "Policy helper is missing."
    [[ -x "$TEMP_DIR/deploy/ochenstarik-smm-emergency" ]] || fail "Emergency recovery command is missing."
    [[ -f "$TEMP_DIR/deploy/$FIREWALL_UNIT" ]] || fail "Mesh firewall unit is missing."
    [[ -x "$TEMP_DIR/bootstrap/ochenstarik-server-monitor-manager.sh" ]] || fail "Packaged bootstrap is missing."
    log "Release archive and checksum are valid."
}

ensure_system_user() {
    local user="$1"
    if ! getent group "$user" >/dev/null; then
        groupadd --system "$user"
    fi
    if ! id "$user" >/dev/null 2>&1; then
        useradd --system --gid "$user" --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin "$user"
    fi
}

ensure_mesh_packages() {
    local missing=0 command_name
    for command_name in wg wg-quick nft ip; do
        command -v "$command_name" >/dev/null 2>&1 || missing=1
    done
    (( missing == 0 )) && return
    require_command apt-get
    log "Installing WireGuard/nftables dependencies."
    DEBIAN_FRONTEND=noninteractive apt-get update
    DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        wireguard-tools nftables iproute2
}

install_tree_atomic() {
    local source="$1" destination="$2" owner="$3" staging
    [[ -d "$source" ]] || fail "Release payload is missing: $source"
    staging="${destination}.new.$$"
    rm -rf -- "$staging"
    install -d -m 0755 "$staging"
    cp -a -- "$source/." "$staging/"
    chown -R "$owner" "$staging"
    find "$staging" -type d -exec chmod 0755 {} +
    find "$staging" -type f -exec chmod 0644 {} +
    find "$staging" -type f -name 'ochenstarik-smm-*' -exec chmod 0755 {} +
    rm -rf -- "$destination"
    mv -- "$staging" "$destination"
}

create_backup() {
    local role="$1" backup_id archive list=()
    backup_id="$(date -u +%Y%m%dT%H%M%SZ)-${role}-$RANDOM"
    install -d -m 0700 "$BACKUP_DIR"
    archive="$BACKUP_DIR/${backup_id}.tar.gz"
    case "$role" in
        control)
            list=(
                "usr/local/lib/ochenstarik-server-monitor-manager/control"
                "etc/ochenstarik-server-monitor-manager/control.env"
                "etc/ochenstarik-server-monitor-manager/control-ca.pfx"
                "etc/ochenstarik-server-monitor-manager/control-server.pfx"
                "etc/systemd/system/$CONTROL_UNIT"
                "usr/local/libexec/ochenstarik-smm-policy-apply"
                "etc/sudoers.d/ochenstarik-smm-control"
            )
            ;;
        agent)
            list=(
                "usr/local/lib/ochenstarik-server-monitor-manager/agent"
                "usr/local/lib/ochenstarik-server-monitor-manager/provisioning-helper"
                "etc/ochenstarik-server-monitor-manager/agent.env"
                "etc/ochenstarik-server-monitor-manager/control-ca.crt"
                "etc/systemd/system/$AGENT_UNIT"
                "etc/systemd/system/$PROVISIONING_HELPER_UNIT"
            )
            ;;
        *) fail "Unknown backup role: $role" ;;
    esac
    local existing=() item
    for item in "${list[@]}"; do
        [[ -e "/$item" ]] && existing+=("$item")
    done
    if (( ${#existing[@]} == 0 )); then
        printf '%s\n' "empty" >"$BACKUP_DIR/${backup_id}.empty"
    else
        tar -C / -czf "$archive" -- "${existing[@]}"
        chmod 0600 "$archive"
    fi
    printf '%s\n' "$backup_id"
}

install_unit() {
    local source="$1" unit="$2"
    install -m 0644 "$source" "/etc/systemd/system/$unit"
    systemctl daemon-reload
}

install_bootstrap_command() {
    local source="$TEMP_DIR/bootstrap/ochenstarik-server-monitor-manager.sh" staging
    [[ -x "$source" ]] || fail "Packaged bootstrap is missing."
    install -d -m 0755 "$(dirname "$BOOTSTRAP_COMMAND")"
    staging="$(mktemp "$(dirname "$BOOTSTRAP_COMMAND")/.ochenstarik-server-monitor-manager.XXXXXXXX")"
    if ! install -m 0755 -o root -g root "$source" "$staging"; then
        rm -f -- "$staging"
        fail "Could not stage the system bootstrap command."
    fi
    if ! mv -fT -- "$staging" "$BOOTSTRAP_COMMAND"; then
        rm -f -- "$staging"
        fail "Could not publish the system bootstrap command."
    fi
}

validate_control_state_migration() {
    local name
    for name in control.db control.db-wal control.db-shm; do
        [[ ! -e "$STATE_DIR/$name" || ! -e "$STATE_DIR/control/$name" ]] \
            || fail "Both legacy and role-isolated Control state exist: $name"
    done
    [[ ! -e "$STATE_DIR/backups" || ! -e "$STATE_DIR/control/backups" ]] \
        || fail "Both legacy and role-isolated Control backup directories exist."
}

record_control_legacy_state() {
    local name
    CONTROL_UPDATE_LEGACY_ITEMS=()
    for name in control.db control.db-wal control.db-shm; do
        [[ ! -e "$STATE_DIR/$name" ]] || CONTROL_UPDATE_LEGACY_ITEMS+=("$name")
    done
    [[ ! -e "$STATE_DIR/backups" ]] || CONTROL_UPDATE_LEGACY_ITEMS+=(backups)
}

prepare_control_state() {
    local name
    install -d -m 0700 -o "$CONTROL_USER" -g "$CONTROL_USER" "$STATE_DIR/control"
    for name in control.db control.db-wal control.db-shm; do
        if [[ -e "$STATE_DIR/$name" ]]; then
            mv -- "$STATE_DIR/$name" "$STATE_DIR/control/$name"
        fi
    done
    if [[ -e "$STATE_DIR/backups" ]]; then
        mv -- "$STATE_DIR/backups" "$STATE_DIR/control/backups"
    fi
    install -d -m 0700 -o "$CONTROL_USER" -g "$CONTROL_USER" "$STATE_DIR/control/backups"
    chown -R "$CONTROL_USER:$CONTROL_USER" "$STATE_DIR/control"
    find "$STATE_DIR/control" -type d -exec chmod 0700 {} +
    find "$STATE_DIR/control" -type f -exec chmod 0600 {} +
}

reverse_control_state_migration() {
    local name source destination
    for name in "${CONTROL_UPDATE_LEGACY_ITEMS[@]}"; do
        source="$STATE_DIR/control/$name"
        destination="$STATE_DIR/$name"
        if [[ -e "$destination" ]]; then
            [[ ! -e "$source" ]] || return 1
            continue
        fi
        [[ ! -e "$source" ]] || mv -- "$source" "$destination" || return 1
    done
    rmdir "$STATE_DIR/control/backups" 2>/dev/null || true
    rmdir "$STATE_DIR/control" 2>/dev/null || true
}

restore_control_update_backup() {
    local archive="$1" restore_root="${2:-/}"
    [[ -f "$archive" ]] || return 1
    tar -C "$restore_root" -xzf "$archive"
}

restore_control_binary_from_archive() {
    local archive="$1" restore_root="${2:-/}"
    [[ -f "$archive" ]] || return 1
    tar -C "$restore_root" -xzf "$archive" \
        usr/local/lib/ochenstarik-server-monitor-manager/control
}

recover_control_update() {
    local backup_id="$1" restore_root="${2:-/}"
    local archive="$BACKUP_DIR/${backup_id}.tar.gz"
    systemctl stop "$CONTROL_UNIT" 2>/dev/null || true
    reverse_control_state_migration || return 1
    restore_control_update_backup "$archive" "$restore_root" || return 1
    systemctl daemon-reload || return 1
    systemctl start "$CONTROL_UNIT" || return 1
    systemctl is-active --quiet "$CONTROL_UNIT"
}

validate_control_environment_migration() {
    local env_file="$ETC_DIR/control.env" database_count backup_count database_value backup_value
    [[ -f "$env_file" && ! -L "$env_file" ]] \
        || fail "Control environment is missing or unsafe."
    database_count="$(grep -c '^Control__DatabasePath=' "$env_file" || true)"
    backup_count="$(grep -c '^Control__BackupDirectory=' "$env_file" || true)"
    [[ "$database_count" == 1 && "$backup_count" == 1 ]] \
        || fail "Control environment contains missing or conflicting state paths."
    database_value="$(grep '^Control__DatabasePath=' "$env_file")"
    backup_value="$(grep '^Control__BackupDirectory=' "$env_file")"
    case "$database_value" in
        "Control__DatabasePath=$STATE_DIR/control.db"|"Control__DatabasePath=$STATE_DIR/control/control.db") ;;
        *) fail "Control environment contains an unsupported database path." ;;
    esac
    case "$backup_value" in
        "Control__BackupDirectory=$STATE_DIR/backups"|"Control__BackupDirectory=$STATE_DIR/control/backups") ;;
        *) fail "Control environment contains an unsupported backup path." ;;
    esac
}

rewrite_control_environment() {
    local env_file="$ETC_DIR/control.env" staging line
    staging="$(mktemp "$ETC_DIR/.control.env.XXXXXXXX")"
    if ! while IFS= read -r line || [[ -n "$line" ]]; do
        case "$line" in
            Control__DatabasePath=*) printf 'Control__DatabasePath=%s/control/control.db\n' "$STATE_DIR" ;;
            Control__BackupDirectory=*) printf 'Control__BackupDirectory=%s/control/backups\n' "$STATE_DIR" ;;
            *) printf '%s\n' "$line" ;;
        esac
    done <"$env_file" >"$staging"; then
        rm -f -- "$staging"
        fail "Could not rewrite the Control environment."
    fi
    chown root:"$CONTROL_USER" "$staging"
    chmod 0640 "$staging"
    if ! mv -fT -- "$staging" "$env_file"; then
        rm -f -- "$staging"
        fail "Could not publish the Control environment."
    fi
}

write_mesh_firewall() {
    cat >"$ETC_DIR/mesh.nft" <<'EOF'
table inet ochenstarik_smm {
    chain links {
        ct state established,related accept
        counter drop
    }

    chain mesh_forward {
        type filter hook forward priority filter; policy accept;
        iifname "smm0" oifname "smm0" jump links
    }
    }
EOF
    chmod 0644 "$ETC_DIR/mesh.nft"
    if ! nft list table inet ochenstarik_smm >/dev/null 2>&1; then
        nft --check -f "$ETC_DIR/mesh.nft"
    fi
}

read_mesh_value() {
    local key="$1"
    [[ -r "$ETC_DIR/mesh.env" ]] || fail "Mesh Hub is not initialized."
    awk -F '=' -v key="$key" '$1 == key { print substr($0, index($0, "=") + 1); exit }' "$ETC_DIR/mesh.env"
}

render_hub_wireguard_config() {
    local private_key endpoint port node_id address public_key status
    private_key="$(cat "$WG_DIR/hub.key")"
    endpoint="$(read_mesh_value HUB_ENDPOINT)"
    port="${endpoint##*:}"
    cat >"/etc/wireguard/smm0.conf" <<EOF
[Interface]
Address = $HUB_MESH_ADDRESS
ListenPort = $port
PrivateKey = $private_key
SaveConfig = false
EOF
    if [[ -r "$MESH_DIR/nodes.tsv" ]]; then
        while IFS=$'\t' read -r node_id address public_key status; do
            [[ "$status" == "active" ]] || continue
            cat >>"/etc/wireguard/smm0.conf" <<EOF

# Node: $node_id
[Peer]
PublicKey = $public_key
AllowedIPs = $address/32
EOF
        done <"$MESH_DIR/nodes.tsv"
    fi
    chmod 0600 "/etc/wireguard/smm0.conf"
}

mesh_init() {
    local public_endpoint="$1" port="${2:-51820}" hub_private hub_public
    require_root
    validate_platform
    validate_port "$port"
    if [[ "$public_endpoint" == *:* ]]; then
        fail "Use an IPv4 address or DNS name without a port for PUBLIC_ENDPOINT."
    fi
    [[ "$public_endpoint" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$ ]] \
        || fail "Invalid WireGuard public endpoint."
    ensure_mesh_packages
    install -d -m 0700 "$WG_DIR" "$MESH_DIR" /etc/wireguard
    if [[ ! -f "$WG_DIR/hub.key" ]]; then
        umask 077
        wg genkey >"$WG_DIR/hub.key"
    fi
    hub_private="$(cat "$WG_DIR/hub.key")"
    hub_public="$(printf '%s' "$hub_private" | wg pubkey)"
    printf '%s\n' "$hub_public" >"$WG_DIR/hub.pub"
    chmod 0600 "$WG_DIR/hub.key"
    chmod 0644 "$WG_DIR/hub.pub"
    cat >"$ETC_DIR/mesh.env" <<EOF
HUB_ENDPOINT=$public_endpoint:$port
HUB_PUBLIC_KEY=$hub_public
MESH_NETWORK=$MESH_NETWORK
EOF
    chmod 0644 "$ETC_DIR/mesh.env"
    touch "$MESH_DIR/nodes.tsv"
    chmod 0600 "$MESH_DIR/nodes.tsv"
    printf '%s\n' 'net.ipv4.ip_forward=1' >"/etc/sysctl.d/90-ochenstarik-smm-mesh.conf"
    sysctl --system >/dev/null
    write_mesh_firewall
    [[ -f "$LIB_DIR/control/ochenstarik-smm-control" ]] \
        || log "Warning: Control is not installed yet; mesh peer codes require Control enrollment."
    if [[ -f "${TEMP_DIR:-}/deploy/$FIREWALL_UNIT" ]]; then
        install_unit "$TEMP_DIR/deploy/$FIREWALL_UNIT" "$FIREWALL_UNIT"
    elif [[ -f "$LIB_DIR/bootstrap/$FIREWALL_UNIT" ]]; then
        install_unit "$LIB_DIR/bootstrap/$FIREWALL_UNIT" "$FIREWALL_UNIT"
    else
        fail "Mesh firewall systemd unit is unavailable; reinstall Control from the current release."
    fi
    systemctl enable "$FIREWALL_UNIT"
    systemctl restart "$FIREWALL_UNIT"
    render_hub_wireguard_config
    systemctl enable wg-quick@smm0.service
    systemctl restart wg-quick@smm0.service
    log "Mesh Hub initialized at $public_endpoint:$port with $MESH_NETWORK."
    log "WireGuard public key: $hub_public"
}

reserve_node_address() {
    local node_id="$1" existing host address
    install -d -m 0700 "$MESH_DIR"
    touch "$MESH_DIR/nodes.tsv"
    chmod 0600 "$MESH_DIR/nodes.tsv"
    existing="$(awk -F '\t' -v node="$node_id" '$1 == node { print $2; exit }' "$MESH_DIR/nodes.tsv")"
    if [[ -n "$existing" ]]; then
        printf '%s\n' "$existing"
        return
    fi
    for host in $(seq 2 254); do
        address="10.77.0.$host"
        if ! awk -F '\t' -v address="$address" '$2 == address { found=1 } END { exit found ? 0 : 1 }' "$MESH_DIR/nodes.tsv"; then
            printf '%s\t%s\t-\treserved\n' "$node_id" "$address" >>"$MESH_DIR/nodes.tsv"
            printf '%s\n' "$address"
            return
        fi
    done
    fail "Mesh address pool is exhausted."
}

configure_node_wireguard() {
    local node_id="$1" node_address="$2" hub_endpoint="$3" hub_public_key="$4" node_private node_public
    ensure_mesh_packages
    install -d -m 0700 "$WG_DIR" /etc/wireguard
    if [[ ! -f "$WG_DIR/node.key" ]]; then
        umask 077
        wg genkey >"$WG_DIR/node.key"
    fi
    node_private="$(cat "$WG_DIR/node.key")"
    node_public="$(printf '%s' "$node_private" | wg pubkey)"
    printf '%s\n' "$node_public" >"$WG_DIR/node.pub"
    cat >"/etc/wireguard/smm0.conf" <<EOF
[Interface]
Address = $node_address/32
PrivateKey = $node_private
SaveConfig = false

[Peer]
PublicKey = $hub_public_key
Endpoint = $hub_endpoint
AllowedIPs = $MESH_NETWORK
PersistentKeepalive = 25
EOF
    chmod 0600 "$WG_DIR/node.key" /etc/wireguard/smm0.conf
    chmod 0644 "$WG_DIR/node.pub"
    systemctl enable wg-quick@smm0.service
    systemctl restart wg-quick@smm0.service
    MESH_PEER_CODE="$(printf 'SMMPEER1.%s.%s.%s' \
        "$(printf '%s' "$node_id" | base64url_encode)" \
        "$(printf '%s' "$node_address" | base64url_encode)" \
        "$(printf '%s' "$node_public" | base64url_encode)")"
}

create_control_certificates() {
    local public_host="$1" ca_key ca_cert serial_file server_key server_csr ext_file san
    ca_key="$TEMP_DIR/control-ca.key"
    ca_cert="$TEMP_DIR/control-ca.crt"
    serial_file="$TEMP_DIR/control-ca.srl"
    server_key="$TEMP_DIR/control-server.key"
    server_csr="$TEMP_DIR/control-server.csr"
    ext_file="$TEMP_DIR/control-server.ext"
    if [[ "$public_host" == *:* || "$public_host" =~ ^[0-9]+(\.[0-9]+){3}$ ]]; then
        san="IP:$public_host"
    else
        [[ "$public_host" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$ ]] \
            || fail "Invalid public host name."
        san="DNS:$public_host"
    fi

    openssl ecparam -name prime256v1 -genkey -noout -out "$ca_key"
    openssl req -x509 -new -sha256 -days 3650 -key "$ca_key" -out "$ca_cert" \
        -subj "/CN=Server Monitor Manager Control CA" \
        -addext "basicConstraints=critical,CA:TRUE" \
        -addext "keyUsage=critical,keyCertSign,cRLSign"
    openssl ecparam -name prime256v1 -genkey -noout -out "$server_key"
    openssl req -new -sha256 -key "$server_key" -out "$server_csr" \
        -subj "/CN=$public_host"
    printf '%s\n' \
        "basicConstraints=critical,CA:FALSE" \
        "keyUsage=critical,digitalSignature,keyEncipherment" \
        "extendedKeyUsage=serverAuth" \
        "subjectAltName=$san" >"$ext_file"
    openssl x509 -req -sha256 -days 825 -in "$server_csr" -CA "$ca_cert" -CAkey "$ca_key" \
        -CAserial "$serial_file" -CAcreateserial -out "$TEMP_DIR/control-server.crt" -extfile "$ext_file"
    openssl pkcs12 -export -out "$ETC_DIR/control-ca.pfx" -inkey "$ca_key" -in "$ca_cert" -passout pass:
    openssl pkcs12 -export -out "$ETC_DIR/control-server.pfx" -inkey "$server_key" \
        -in "$TEMP_DIR/control-server.crt" -certfile "$ca_cert" -passout pass:
    install -m 0644 "$ca_cert" "$ETC_DIR/control-ca.crt"
    chown root:"$CONTROL_USER" "$ETC_DIR/control-ca.pfx" "$ETC_DIR/control-server.pfx"
    chmod 0640 "$ETC_DIR/control-ca.pfx" "$ETC_DIR/control-server.pfx"
}

install_control() {
    local archive="$1" public_host="$2" port="${3:-7443}" backup_id
    require_root
    validate_platform
    validate_port "$port"
    require_command openssl
    require_command sha256sum
    require_command tar
    require_command systemctl
    require_command sudo
    require_command visudo
    extract_archive "$archive"
    [[ -x "$TEMP_DIR/control/ochenstarik-smm-control" ]] || fail "Control binary is missing."
    backup_id="$(create_backup control)"
    ensure_system_user "$CONTROL_USER"
    validate_control_state_migration
    systemctl stop "$CONTROL_UNIT" 2>/dev/null || true
    install -d -m 0711 -o root -g root "$ETC_DIR" "$STATE_DIR"
    prepare_control_state
    install_tree_atomic "$TEMP_DIR/control" "$LIB_DIR/control" "root:root"
    install_bootstrap_command
    if [[ ! -f "$ETC_DIR/control-ca.pfx" || ! -f "$ETC_DIR/control-server.pfx" ]]; then
        create_control_certificates "$public_host"
    fi
    cat >"$ETC_DIR/control.env" <<EOF
ASPNETCORE_URLS=https://0.0.0.0:$port
ASPNETCORE_Kestrel__Certificates__Default__Path=$ETC_DIR/control-server.pfx
Control__DatabasePath=$STATE_DIR/control/control.db
Control__CertificateAuthorityPath=$ETC_DIR/control-ca.pfx
Control__BackupDirectory=$STATE_DIR/control/backups
Control__HubHelperPath=$POLICY_HELPER
Control__PrivilegeEscalationPath=/usr/bin/sudo
Control__LinkReconciliationSeconds=300
Control__LinkRetentionDays=90
EOF
    printf '%s\n' "https://$public_host:$port" >"$ETC_DIR/control-public-url"
    chown root:"$CONTROL_USER" "$ETC_DIR/control.env"
    chmod 0640 "$ETC_DIR/control.env"
    chmod 0644 "$ETC_DIR/control-public-url"
    install -d -m 0755 "$(dirname "$POLICY_HELPER")"
    install -m 0755 "$TEMP_DIR/deploy/ochenstarik-smm-policy-apply" "$POLICY_HELPER"
    install -d -m 0755 "$(dirname "$EMERGENCY_COMMAND")"
    install -m 0755 "$TEMP_DIR/deploy/ochenstarik-smm-emergency" "$EMERGENCY_COMMAND"
    install -d -m 0755 "$LIB_DIR/bootstrap"
    install -m 0644 "$TEMP_DIR/deploy/$FIREWALL_UNIT" "$LIB_DIR/bootstrap/$FIREWALL_UNIT"
    printf '%s\n' "$CONTROL_USER ALL=(root) NOPASSWD: $POLICY_HELPER *" >"$SUDOERS_FILE"
    chmod 0440 "$SUDOERS_FILE"
    visudo -cf "$SUDOERS_FILE" >/dev/null
    install_unit "$TEMP_DIR/deploy/$CONTROL_UNIT" "$CONTROL_UNIT"
    systemctl enable --now "$CONTROL_UNIT"
    systemctl is-active --quiet "$CONTROL_UNIT" || {
        systemctl status --no-pager "$CONTROL_UNIT" >&2 || true
        fail "Control service failed; backup is $backup_id"
    }
    log "Control installed. Backup: $backup_id"
    log "CA fingerprint: $(openssl x509 -in "$ETC_DIR/control-ca.crt" -noout -fingerprint -sha256 | cut -d= -f2)"
}

read_enrollment_token() {
    if [[ -n "${SMM_ENROLL_TOKEN:-}" ]]; then
        ENROLL_TOKEN="$SMM_ENROLL_TOKEN"
        unset SMM_ENROLL_TOKEN
        return
    fi
    [[ -t 0 ]] || fail "Set SMM_ENROLL_TOKEN or run from an interactive local terminal."
    read -r -s -p "One-time enrollment token: " ENROLL_TOKEN
    printf '\n'
    [[ -n "$ENROLL_TOKEN" ]] || fail "Enrollment token is empty."
}

install_agent() {
    local archive="$1" node_id="$2" control_url="$3" ca_cert="$4" backup_id token_file token_temp
    require_root
    validate_platform
    validate_node_id "$node_id"
    validate_control_url "$control_url"
    [[ -f "$ca_cert" ]] || fail "Control CA certificate not found: $ca_cert"
    require_command sha256sum
    require_command tar
    require_command systemctl
    require_command runuser
    require_command openssl
    openssl x509 -in "$ca_cert" -noout >/dev/null 2>&1 || fail "Invalid Control CA certificate."
    extract_archive "$archive"
    [[ -x "$TEMP_DIR/agent/ochenstarik-smm-agent" ]] || fail "Agent binary is missing."
    [[ -x "$TEMP_DIR/provisioning-helper/ochenstarik-smm-provisioning-helper" ]] || fail "Provisioning helper binary is missing."
    backup_id="$(create_backup agent)"
    ensure_system_user "$AGENT_USER"
    install -d -m 0711 -o root -g root "$ETC_DIR" "$STATE_DIR"
    install -d -m 0700 -o "$AGENT_USER" -g "$AGENT_USER" "$STATE_DIR/agent"
    install -d -m 0700 -o "$AGENT_USER" -g "$AGENT_USER" "$ENROLLMENT_DIR"
    install -d -m 0700 -o root -g root "$STATE_DIR/provisioning/rollback"
    install_tree_atomic "$TEMP_DIR/agent" "$LIB_DIR/agent" "root:root"
    install_tree_atomic "$TEMP_DIR/provisioning-helper" "$LIB_DIR/provisioning-helper" "root:root"
    install_bootstrap_command
    install -d -m 0755 "$(dirname "$EMERGENCY_COMMAND")"
    install -m 0755 "$TEMP_DIR/deploy/ochenstarik-smm-emergency" "$EMERGENCY_COMMAND"
    if [[ "$(realpath "$ca_cert")" != "$(realpath -m "$ETC_DIR/control-ca.crt")" ]]; then
        install -m 0600 -o "$AGENT_USER" -g "$AGENT_USER" "$ca_cert" "$ETC_DIR/control-ca.crt"
    else
        chown "$AGENT_USER:$AGENT_USER" "$ETC_DIR/control-ca.crt"
        chmod 0600 "$ETC_DIR/control-ca.crt"
    fi
    cat >"$ETC_DIR/agent.env" <<EOF
SMM_NodeId=$node_id
SMM_AgentUid=$(id -u "$AGENT_USER")
SMM_ControlUrl=${control_url%/}
SMM_StateDirectory=$STATE_DIR/agent
SMM_EnrollmentTokenDirectory=$ENROLLMENT_DIR
SMM_CertificateAuthorityPath=$ETC_DIR/control-ca.crt
EOF
    chown root:"$AGENT_USER" "$ETC_DIR/agent.env"
    chmod 0640 "$ETC_DIR/agent.env"
    read_enrollment_token
    token_file="$ENROLLMENT_DIR/enroll-token"
    token_temp="$(mktemp "$ENROLLMENT_DIR/.enroll-token.XXXXXXXX")"
    ENROLLMENT_TOKEN_TEMP="$token_temp"
    if ! printf '%s' "$ENROLL_TOKEN" >"$token_temp"; then
        ENROLL_TOKEN=""
        fail "Could not write the enrollment token file."
    fi
    chown "$AGENT_USER:$AGENT_USER" "$token_temp"
    chmod 0400 "$token_temp"
    ENROLLMENT_TOKEN_FILE="$token_file"
    mv -fT -- "$token_temp" "$token_file"
    ENROLLMENT_TOKEN_TEMP=""
    ENROLL_TOKEN=""
    if ! runuser -u "$AGENT_USER" -- env \
        "SMM_NodeId=$node_id" \
        "SMM_ControlUrl=${control_url%/}" \
        "SMM_StateDirectory=$STATE_DIR/agent" \
        "SMM_EnrollmentTokenDirectory=$ENROLLMENT_DIR" \
        "SMM_CertificateAuthorityPath=$ETC_DIR/control-ca.crt" \
        "SMM_EnrollTokenFile=$token_file" \
        "$LIB_DIR/agent/ochenstarik-smm-agent"; then
        rm -f -- "$token_file"
        fail "Agent enrollment failed."
    fi
    rm -f -- "$token_file"
    ENROLLMENT_TOKEN_FILE=""
    chown root:"$AGENT_USER" "$ETC_DIR/control-ca.crt"
    chmod 0640 "$ETC_DIR/control-ca.crt"
    install_unit "$TEMP_DIR/deploy/$AGENT_UNIT" "$AGENT_UNIT"
    install_unit "$TEMP_DIR/deploy/$PROVISIONING_HELPER_UNIT" "$PROVISIONING_HELPER_UNIT"
    systemctl enable --now "$PROVISIONING_HELPER_UNIT"
    systemctl enable --now "$AGENT_UNIT"
    systemctl is-active --quiet "$AGENT_UNIT" || {
        systemctl status --no-pager "$AGENT_UNIT" >&2 || true
        fail "Agent service failed; backup is $backup_id"
    }
    log "Agent $node_id installed and enrolled. Backup: $backup_id"
}

read_enrollment_code() {
    if [[ -n "${SMM_ENROLL_CODE:-}" ]]; then
        ENROLL_CODE="$SMM_ENROLL_CODE"
        unset SMM_ENROLL_CODE
        return
    fi
    [[ -t 0 ]] || fail "Set SMM_ENROLL_CODE or run from an interactive local terminal."
    read -r -s -p "SMMNODE enrollment code: " ENROLL_CODE
    printf '\n'
    [[ -n "$ENROLL_CODE" ]] || fail "Enrollment code is empty."
}

confirm_ca_fingerprint() {
    local ca_file="$1" answer
    log "Control CA fingerprint: $(openssl x509 -in "$ca_file" -noout -fingerprint -sha256 | cut -d= -f2)"
    if [[ "${SMM_ACCEPT_CA_FINGERPRINT:-}" == "1" ]]; then
        return
    fi
    [[ -t 0 ]] || fail "Set SMM_ACCEPT_CA_FINGERPRINT=1 only after verifying the fingerprint out of band."
    read -r -p "Type 'yes' after comparing this fingerprint with the Hub: " answer
    [[ "$answer" == "yes" ]] || fail "Control CA fingerprint was not confirmed."
}

install_node_from_code() {
    local archive="$1" prefix control_part ca_part node_part token_part
    local endpoint_part hub_key_part address_part network_part extra
    local control_url node_id token ca_file hub_endpoint hub_public_key node_address mesh_network
    require_root
    require_command base64
    require_command openssl
    read_enrollment_code
    IFS='.' read -r prefix control_part ca_part node_part token_part endpoint_part \
        hub_key_part address_part network_part extra <<<"$ENROLL_CODE"
    ENROLL_CODE=""
    [[ "$prefix" == "SMMNODE1" || "$prefix" == "SMMNODE2" ]] \
        || fail "Unsupported SMMNODE enrollment code version."
    [[ -n "$control_part" && -n "$ca_part" && -n "$node_part" && -n "$token_part" \
        && -z "${extra:-}" ]] || fail "Invalid SMMNODE enrollment code."
    if [[ "$prefix" == "SMMNODE1" ]]; then
        [[ -z "${endpoint_part:-}${hub_key_part:-}${address_part:-}${network_part:-}" ]] \
            || fail "Invalid SMMNODE1 enrollment code."
    else
        [[ -n "${endpoint_part:-}" && -n "${hub_key_part:-}" \
            && -n "${address_part:-}" && -n "${network_part:-}" ]] \
            || fail "Invalid SMMNODE2 mesh enrollment code."
    fi
    control_url="$(base64url_decode "$control_part")"
    node_id="$(base64url_decode "$node_part")"
    token="$(base64url_decode "$token_part")"
    if [[ "$prefix" == "SMMNODE2" ]]; then
        hub_endpoint="$(base64url_decode "$endpoint_part")"
        hub_public_key="$(base64url_decode "$hub_key_part")"
        node_address="$(base64url_decode "$address_part")"
        mesh_network="$(base64url_decode "$network_part")"
        [[ "$hub_endpoint" =~ ^[A-Za-z0-9.-]+:[0-9]{1,5}$ ]] || fail "Invalid Hub WireGuard endpoint."
        validate_port "${hub_endpoint##*:}"
        [[ "$hub_public_key" =~ ^[A-Za-z0-9+/]{43}=$ ]] || fail "Invalid Hub WireGuard public key."
        [[ "$node_address" =~ ^10\.77\.0\.([2-9]|[1-9][0-9]|1[0-9]{2}|2[0-4][0-9]|25[0-4])$ ]] \
            || fail "Invalid reserved mesh address."
        [[ "$mesh_network" == "$MESH_NETWORK" ]] || fail "Unsupported mesh network."
    fi
    ca_file="$(mktemp -t smm-control-ca.XXXXXXXX.crt)"
    chmod 0600 "$ca_file"
    base64url_decode "$ca_part" >"$ca_file"
    confirm_ca_fingerprint "$ca_file"
    SMM_ENROLL_TOKEN="$token"
    token=""
    install_agent "$archive" "$node_id" "$control_url" "$ca_file"
    rm -f -- "$ca_file"
    if [[ "$prefix" == "SMMNODE2" ]]; then
        configure_node_wireguard "$node_id" "$node_address" "$hub_endpoint" "$hub_public_key"
        log "Node mesh configured. Copy this public peer code back to the Hub:"
        printf '%s\n' "$MESH_PEER_CODE"
        MESH_PEER_CODE=""
    fi
}

refresh_agent_uid() {
    local env_file="$ETC_DIR/agent.env" agent_uid temp line found=0
    [[ -f "$env_file" && ! -L "$env_file" ]] \
        || fail "Agent environment is missing or unsafe: $env_file"
    agent_uid="$(id -u "$AGENT_USER")"
    temp="$(mktemp "$ETC_DIR/.agent.env.XXXXXXXX")"
    if ! while IFS= read -r line || [[ -n "$line" ]]; do
        if [[ "$line" == SMM_AgentUid=* ]]; then
            printf 'SMM_AgentUid=%s\n' "$agent_uid"
            found=1
        else
            printf '%s\n' "$line"
        fi
    done <"$env_file" >"$temp"; then
        rm -f -- "$temp"
        fail "Could not refresh SMM_AgentUid in agent.env."
    fi
    if [[ "$found" == "0" ]]; then
        printf 'SMM_AgentUid=%s\n' "$agent_uid" >>"$temp"
    fi
    chown root:"$AGENT_USER" "$temp"
    chmod 0640 "$temp"
    mv -fT -- "$temp" "$env_file"
}

update_role() {
    local role="$1" archive="$2" binary unit user backup_id
    require_root
    validate_platform
    extract_archive "$archive"
    case "$role" in
        control) binary="ochenstarik-smm-control"; unit="$CONTROL_UNIT"; user="root:root" ;;
        agent) binary="ochenstarik-smm-agent"; unit="$AGENT_UNIT"; user="root:root" ;;
        *) fail "Unknown role: $role" ;;
    esac
    [[ -x "$TEMP_DIR/$role/$binary" ]] || fail "$role binary is missing."
    
    local manifest="$(dirname "$archive")/server-monitor-manager-manifest.json"
    if [[ -f "$manifest" ]]; then
        local new_version m_control m_agent m_helper
        new_version="$(awk -F'"' '/"version":/ {print $4}' "$manifest" || true)"
        m_control="$(awk -F'"' '/"control":/ {print $4}' "$manifest" || true)"
        m_agent="$(awk -F'"' '/"agent":/ {print $4}' "$manifest" || true)"
        m_helper="$(awk -F'"' '/"helper":/ {print $4}' "$manifest" || true)"
        if [[ -n "$new_version" ]]; then
            if [[ "$new_version" < "$PROGRAM_VERSION" && "${SMM_ALLOW_DOWNGRADE:-0}" != "1" ]]; then
                fail "Downgrade from $PROGRAM_VERSION to $new_version is not allowed. Set SMM_ALLOW_DOWNGRADE=1 to bypass."
            fi
            
            if [[ "$role" == "control" ]] && systemctl list-unit-files | grep -q "^${AGENT_UNIT}"; then
                if [[ "$PROGRAM_VERSION" != "$m_agent" ]]; then
                    fail "Incompatible versions: installed agent is $PROGRAM_VERSION, but archive requires agent $m_agent. Update rejected."
                fi
            elif [[ "$role" == "agent" ]] && systemctl list-unit-files | grep -q "^${CONTROL_UNIT}"; then
                if [[ "$PROGRAM_VERSION" != "$m_control" ]]; then
                    fail "Incompatible versions: installed control is $PROGRAM_VERSION, but archive requires control $m_control. Update rejected."
                fi
            fi

            log "Updating $role to version $new_version"
        fi
    fi
    if [[ "$role" == "control" ]]; then
        validate_control_state_migration
        validate_control_environment_migration
        record_control_legacy_state
    else
        refresh_agent_uid
    fi
    backup_id="$(create_backup "$role")"
    if [[ "$role" == "control" ]]; then
        CONTROL_UPDATE_BACKUP_ID="$backup_id"
        CONTROL_UPDATE_RECOVERY_REQUIRED=1
    fi
    systemctl stop "$unit"
    if [[ "$role" == "agent" ]]; then
        systemctl stop "$PROVISIONING_HELPER_UNIT" 2>/dev/null || true
    fi
    if [[ "$role" == "control" ]]; then
        install -d -m 0711 -o root -g root "$ETC_DIR" "$STATE_DIR"
        prepare_control_state
        rewrite_control_environment
        install_bootstrap_command
        install_unit "$TEMP_DIR/deploy/$CONTROL_UNIT" "$CONTROL_UNIT"
        systemctl enable "$CONTROL_UNIT"
        install_tree_atomic "$TEMP_DIR/control" "$LIB_DIR/control" "$user"
    else
        install_tree_atomic "$TEMP_DIR/$role" "$LIB_DIR/$role" "$user"
        [[ -x "$TEMP_DIR/provisioning-helper/ochenstarik-smm-provisioning-helper" ]] \
            || fail "Provisioning helper binary is missing."
        install_tree_atomic "$TEMP_DIR/provisioning-helper" "$LIB_DIR/provisioning-helper" "root:root"
        install_unit "$TEMP_DIR/deploy/$AGENT_UNIT" "$AGENT_UNIT"
        install_unit "$TEMP_DIR/deploy/$PROVISIONING_HELPER_UNIT" "$PROVISIONING_HELPER_UNIT"
        systemctl enable --now "$PROVISIONING_HELPER_UNIT"
    fi
    systemctl restart "$unit"
    if ! systemctl is-active --quiet "$unit"; then
        if [[ "$role" == "agent" ]]; then
            log "Update failed; restoring backup $backup_id"
            restore_backup "$role" "$backup_id"
        fi
        fail "$role update was rolled back."
    fi
    if [[ "$role" == "control" ]]; then
        CONTROL_UPDATE_RECOVERY_REQUIRED=0
        CONTROL_UPDATE_BACKUP_ID=""
        CONTROL_UPDATE_LEGACY_ITEMS=()
    fi
    log "$role updated. Backup: $backup_id"
}

latest_backup_id() {
    local role="$1" path
    path="$(find "$BACKUP_DIR" -maxdepth 1 -type f \( -name "*-${role}-*.tar.gz" -o -name "*-${role}-*.empty" \) -printf '%f\n' 2>/dev/null | sort | tail -n1)"
    [[ -n "$path" ]] || fail "No backup found for $role."
    printf '%s\n' "${path%.tar.gz}" | sed 's/\.empty$//'
}

restore_backup() {
    local role="$1" backup_id="$2" archive="$BACKUP_DIR/${backup_id}.tar.gz" unit
    case "$role" in control) unit="$CONTROL_UNIT" ;; agent) unit="$AGENT_UNIT" ;; *) fail "Unknown role: $role" ;; esac
    [[ -f "$archive" ]] || fail "Backup archive not found: $backup_id"
    systemctl stop "$unit" || true
    if [[ "$role" == "agent" ]]; then
        systemctl stop "$PROVISIONING_HELPER_UNIT" || true
    fi
    if [[ "$role" == "control" ]]; then
        restore_control_binary_from_archive "$archive"
    else
        tar -C / -xzf "$archive"
    fi
    systemctl daemon-reload
    if [[ "$role" == "agent" ]]; then
        systemctl start "$PROVISIONING_HELPER_UNIT"
    fi
    systemctl start "$unit"
    systemctl is-active --quiet "$unit" || fail "Rollback restored files but service is not active."
    log "$role restored from $backup_id"
}

rollback_role() {
    local role="$1" backup_id="${2:-}"
    require_root
    [[ -n "$backup_id" ]] || backup_id="$(latest_backup_id "$role")"
    restore_backup "$role" "$backup_id"
}

show_status() {
    local unit
    for unit in "$CONTROL_UNIT" "$AGENT_UNIT"; do
        if systemctl list-unit-files "$unit" --no-legend 2>/dev/null | grep -q "^$unit"; then
            printf '%s: %s\n' "$unit" "$(systemctl is-active "$unit" 2>/dev/null || true)"
        else
            printf '%s: not-installed\n' "$unit"
        fi
    done
    if [[ -f "$ETC_DIR/control-ca.crt" ]] && command -v openssl >/dev/null; then
        printf 'control-ca: %s\n' "$(openssl x509 -in "$ETC_DIR/control-ca.crt" -noout -fingerprint -sha256 | cut -d= -f2)"
    fi
}

run_control_cli() {
    local command_name="$1" identifier="$2"
    require_root
    validate_node_id "$identifier"
    [[ -x "$LIB_DIR/control/ochenstarik-smm-control" ]] || fail "Control is not installed."
    [[ -f "$ETC_DIR/control.env" ]] || fail "Control environment is missing."
    require_command systemd-run
    systemd-run --wait --pipe --quiet --collect \
        --uid="$CONTROL_USER" \
        --gid="$CONTROL_USER" \
        -p "EnvironmentFile=$ETC_DIR/control.env" \
        "$LIB_DIR/control/ochenstarik-smm-control" "$command_name" "$identifier"
}

create_node_code() {
    local node_id="$1" token control_url ca_pem node_address hub_endpoint hub_public_key mesh_network
    require_root
    validate_node_id "$node_id"
    [[ -r "$ETC_DIR/control-public-url" ]] || fail "Control public URL is missing; reinstall Control with PUBLIC_HOST."
    [[ -r "$ETC_DIR/control-ca.crt" ]] || fail "Control CA certificate is missing."
    require_command base64
    control_url="$(tr -d '\r\n' <"$ETC_DIR/control-public-url")"
    validate_control_url "$control_url"
    token="$(run_control_cli token-create "$node_id")"
    [[ -n "$token" && "$token" != *$'\n'* ]] || fail "Control returned an invalid enrollment token."
    ca_pem="$(cat "$ETC_DIR/control-ca.crt")"
    if [[ -r "$ETC_DIR/mesh.env" && -r "$WG_DIR/hub.pub" ]]; then
        node_address="$(reserve_node_address "$node_id")"
        hub_endpoint="$(read_mesh_value HUB_ENDPOINT)"
        hub_public_key="$(read_mesh_value HUB_PUBLIC_KEY)"
        mesh_network="$(read_mesh_value MESH_NETWORK)"
        printf 'SMMNODE2.%s.%s.%s.%s.%s.%s.%s.%s\n' \
            "$(printf '%s' "$control_url" | base64url_encode)" \
            "$(printf '%s' "$ca_pem" | base64url_encode)" \
            "$(printf '%s' "$node_id" | base64url_encode)" \
            "$(printf '%s' "$token" | base64url_encode)" \
            "$(printf '%s' "$hub_endpoint" | base64url_encode)" \
            "$(printf '%s' "$hub_public_key" | base64url_encode)" \
            "$(printf '%s' "$node_address" | base64url_encode)" \
            "$(printf '%s' "$mesh_network" | base64url_encode)"
    else
        printf 'SMMNODE1.%s.%s.%s.%s\n' \
            "$(printf '%s' "$control_url" | base64url_encode)" \
            "$(printf '%s' "$ca_pem" | base64url_encode)" \
            "$(printf '%s' "$node_id" | base64url_encode)" \
            "$(printf '%s' "$token" | base64url_encode)"
    fi
    token=""
}

create_device_code() {
    local device_id="$1" token control_url ca_der
    require_root
    validate_node_id "$device_id"
    [[ -r "$ETC_DIR/control-public-url" ]] || fail "Control public URL is missing; reinstall Control with PUBLIC_HOST."
    [[ -r "$ETC_DIR/control-ca.crt" ]] || fail "Control CA certificate is missing."
    require_command base64
    require_command openssl
    control_url="$(tr -d '\r\n' <"$ETC_DIR/control-public-url")"
    validate_control_url "$control_url"
    token="$(run_control_cli device-token-create "$device_id")"
    [[ "$token" =~ ^[A-Za-z0-9_-]{43}$ ]] \
        || fail "Control returned an invalid device enrollment token."
    ca_der="$(openssl x509 -in "$ETC_DIR/control-ca.crt" -outform DER | base64 -w 0)"
    printf 'SMMDEV1-'
    printf 'VERSION=1\nDEVICE=%s\nTOKEN=%s\nURL=%s\nCA=%s\n' \
        "$device_id" "$token" "$control_url" "$ca_der" | base64url_encode
    printf '\n'
    token=""
}

add_mesh_peer() {
    local code="$1" prefix node_part address_part key_part extra
    local node_id address public_key current tmp
    require_root
    require_command base64
    require_command wg
    [[ -r "$ETC_DIR/mesh.env" && -r "$MESH_DIR/nodes.tsv" ]] || fail "Mesh Hub is not initialized."
    IFS='.' read -r prefix node_part address_part key_part extra <<<"$code"
    [[ "$prefix" == "SMMPEER1" && -n "$node_part" && -n "$address_part" \
        && -n "$key_part" && -z "${extra:-}" ]] || fail "Invalid SMMPEER1 code."
    node_id="$(base64url_decode "$node_part")"
    address="$(base64url_decode "$address_part")"
    public_key="$(base64url_decode "$key_part")"
    validate_node_id "$node_id"
    [[ "$address" =~ ^10\.77\.0\.([2-9]|[1-9][0-9]|1[0-9]{2}|2[0-4][0-9]|25[0-4])$ ]] \
        || fail "Invalid peer mesh address."
    [[ "$public_key" =~ ^[A-Za-z0-9+/]{43}=$ ]] || fail "Invalid peer WireGuard public key."
    current="$(awk -F '\t' -v node="$node_id" '$1 == node { print $2; exit }' "$MESH_DIR/nodes.tsv")"
    [[ "$current" == "$address" ]] || fail "Peer address does not match the Hub reservation."
    if awk -F '\t' -v address="$address" -v node="$node_id" '$2 == address && $1 != node { found=1 } END { exit found ? 0 : 1 }' "$MESH_DIR/nodes.tsv"; then
        fail "Peer mesh address is already assigned."
    fi
    tmp="$(mktemp -p "$MESH_DIR" nodes.tsv.XXXXXXXX)"
    awk -F '\t' -v OFS='\t' -v node="$node_id" -v address="$address" -v key="$public_key" \
        '$1 == node { print node, address, key, "active"; found=1; next } { print } END { if (!found) exit 1 }' \
        "$MESH_DIR/nodes.tsv" >"$tmp" || { rm -f -- "$tmp"; fail "Peer reservation is missing."; }
    chmod 0600 "$tmp"
    mv -- "$tmp" "$MESH_DIR/nodes.tsv"
    render_hub_wireguard_config
    systemctl restart wg-quick@smm0.service
    systemctl is-active --quiet wg-quick@smm0.service || fail "WireGuard failed after peer registration."
    log "Mesh peer $node_id activated at $address."
}

show_mesh_status() {
    require_root
    [[ -r "$ETC_DIR/mesh.env" ]] || fail "Mesh Hub is not initialized."
    printf 'endpoint: %s\n' "$(read_mesh_value HUB_ENDPOINT)"
    printf 'network: %s\n' "$(read_mesh_value MESH_NETWORK)"
    if [[ -r "$MESH_DIR/nodes.tsv" ]]; then
        printf '%-24s %-15s %-10s %s\n' NODE ADDRESS STATUS HANDSHAKE
        while IFS=$'\t' read -r node_id address public_key status; do
            [[ -n "$node_id" ]] || continue
            local handshake="-"
            if [[ "$status" == "active" ]]; then
                handshake="$(wg show smm0 latest-handshakes 2>/dev/null | awk -v key="$public_key" '$1 == key { print $2; exit }')"
                [[ -n "$handshake" && "$handshake" != "0" ]] || handshake="never"
            fi
            printf '%-24s %-15s %-10s %s\n' "$node_id" "$address" "$status" "$handshake"
        done <"$MESH_DIR/nodes.tsv"
    fi
}

show_ca_fingerprint() {
    [[ -f "$ETC_DIR/control-ca.crt" ]] || fail "Control CA certificate is not installed."
    require_command openssl
    openssl x509 -in "$ETC_DIR/control-ca.crt" -noout -fingerprint -sha256
}

role_is_installed() {
    case "$1" in
        control) [[ -d "$LIB_DIR/control" || -f "$ETC_DIR/control.env" \
            || -f "/etc/systemd/system/${CONTROL_UNIT:-ochenstarik-smm-control.service}" ]] ;;
        agent) [[ -d "$LIB_DIR/agent" || -f "$ETC_DIR/agent.env" \
            || -f "/etc/systemd/system/${AGENT_UNIT:-ochenstarik-smm-agent.service}" ]] ;;
        *) return 1 ;;
    esac
}

remove_shared_ca_if_unused() {
    if ! role_is_installed control && ! role_is_installed agent; then
        rm -f -- "$ETC_DIR/control-ca.crt"
    fi
}

uninstall_agent() {
    local purge="${1:-}"
    require_root
    systemctl disable --now "$AGENT_UNIT" 2>/dev/null || true
    systemctl disable --now "$PROVISIONING_HELPER_UNIT" 2>/dev/null || true
    rm -f -- "/etc/systemd/system/$AGENT_UNIT" "/etc/systemd/system/$PROVISIONING_HELPER_UNIT" "$ETC_DIR/agent.env"
    rm -rf -- "$LIB_DIR/agent" "$LIB_DIR/provisioning-helper"
    if [[ "$purge" == "--purge" ]]; then
        rm -rf -- "$STATE_DIR/agent" "$ENROLLMENT_DIR"
        remove_shared_ca_if_unused
    fi
    systemctl daemon-reload
    log "Agent removed${purge:+ ($purge)}."
}

install_monitor() {
    local public_key="$1"
    require_root
    validate_platform
    
    local metrics_script="/usr/local/libexec/ochenstarik-smm-metrics"
    local monitor_user="ochenstarik-monitor"
    local monitor_home="/var/lib/ochenstarik-monitor"
    
    if ! id -u "$monitor_user" >/dev/null 2>&1; then
        useradd -r -s /usr/sbin/nologin -d "$monitor_home" -M "$monitor_user"
    fi
    install -d -m 0755 -o "$monitor_user" -g "$monitor_user" "$monitor_home"
    
    install -d -m 0755 "/usr/local/libexec"
    cat >"$metrics_script" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
# ochenstarik-smm-metrics

echo "PROTOCOL=1"
echo "HOSTNAME=$(hostname)"
UPTIME=$(awk '{print int($1)}' /proc/uptime 2>/dev/null || echo "0")
echo "UPTIME_SECONDS=${UPTIME}"
LOAD1=$(awk '{print $1}' /proc/loadavg 2>/dev/null || echo "0.00")
echo "LOAD1=${LOAD1}"
CPU_COUNT=$(nproc 2>/dev/null || echo "1")
echo "CPU_COUNT=${CPU_COUNT}"
MEM_TOTAL=$(awk '/^MemTotal:/ {print $2}' /proc/meminfo 2>/dev/null || echo "0")
echo "MEM_TOTAL_KB=${MEM_TOTAL}"
MEM_AVAIL=$(awk '/^MemAvailable:/ {print $2}' /proc/meminfo 2>/dev/null || echo "0")
if [ "$MEM_AVAIL" = "0" ] || [ -z "$MEM_AVAIL" ]; then
    MEM_FREE=$(awk '/^MemFree:/ {print $2}' /proc/meminfo 2>/dev/null || echo "0")
    MEM_CACHED=$(awk '/^Cached:/ {print $2}' /proc/meminfo 2>/dev/null || echo "0")
    MEM_AVAIL=$((MEM_FREE + MEM_CACHED))
fi
echo "MEM_AVAILABLE_KB=${MEM_AVAIL}"
SWAP_TOTAL=$(awk '/^SwapTotal:/ {print $2}' /proc/meminfo 2>/dev/null || echo "0")
echo "SWAP_TOTAL_KB=${SWAP_TOTAL}"
SWAP_FREE=$(awk '/^SwapFree:/ {print $2}' /proc/meminfo 2>/dev/null || echo "0")
echo "SWAP_FREE_KB=${SWAP_FREE}"
DF_OUT=$(df -k / 2>/dev/null | awk 'NR==2 {print $2, $4}' || echo "0 0")
DISK_TOTAL=$(echo "$DF_OUT" | awk '{print $1}')
DISK_AVAIL=$(echo "$DF_OUT" | awk '{print $2}')
echo "DISK_TOTAL_KB=${DISK_TOTAL}"
echo "DISK_AVAILABLE_KB=${DISK_AVAIL}"
DF_INODES=$(df -i / 2>/dev/null | awk 'NR==2 {print $2, $4}' || echo "0 0")
INODES_TOTAL=$(echo "$DF_INODES" | awk '{print $1}')
INODES_FREE=$(echo "$DF_INODES" | awk '{print $2}')
echo "DISK_INODES_TOTAL=${INODES_TOTAL}"
echo "DISK_INODES_FREE=${INODES_FREE}"
NET_RX=$(awk 'NR>2 {rx+=$1} END {print rx}' /proc/net/dev 2>/dev/null || echo "0")
NET_TX=$(awk 'NR>2 {tx+=$9} END {print tx}' /proc/net/dev 2>/dev/null || echo "0")
echo "NETWORK_RX_BYTES=${NET_RX}"
echo "NETWORK_TX_BYTES=${NET_TX}"
KERNEL=$(uname -r 2>/dev/null || echo "unknown")
echo "KERNEL=${KERNEL}"
SYSTEMD_SSH=$(systemctl is-active ssh.service 2>/dev/null || true)
echo "SYSTEMD_SSH=${SYSTEMD_SSH:-unknown}"
SYSTEMD_WIREGUARD=$(systemctl is-active wg-quick@smm0.service 2>/dev/null || true)
echo "SYSTEMD_WIREGUARD=${SYSTEMD_WIREGUARD:-unknown}"
EOF
    chown root:root "$metrics_script"
    chmod 0755 "$metrics_script"

    install -d -m 0700 -o "$monitor_user" -g "$monitor_user" "$monitor_home/.ssh"
    local auth_keys="$monitor_home/.ssh/authorized_keys"
    
    # We idempotently add the key
    local key_entry="command=\"$metrics_script\",restrict,no-pty,no-agent-forwarding,no-port-forwarding,no-X11-forwarding $public_key"
    if [[ -f "$auth_keys" ]] && grep -qF "$public_key" "$auth_keys"; then
        log "Monitor key already installed."
    else
        echo "$key_entry" >> "$auth_keys"
        chown "$monitor_user:$monitor_user" "$auth_keys"
        chmod 0600 "$auth_keys"
        log "Monitor key installed."
    fi
}

uninstall_monitor() {
    require_root
    local monitor_user="ochenstarik-monitor"
    local monitor_home="/var/lib/ochenstarik-monitor"
    local metrics_script="/usr/local/libexec/ochenstarik-smm-metrics"

    if id -u "$monitor_user" >/dev/null 2>&1; then
        userdel -f "$monitor_user" || true
    fi
    rm -rf -- "$monitor_home"
    rm -f -- "$metrics_script"
    log "Monitor removed."
}

uninstall_control() {
    [[ "${1:-}" == "--confirm-destroy-control" ]] || fail "Control removal requires --confirm-destroy-control"
    require_root
    systemctl disable --now "$CONTROL_UNIT" 2>/dev/null || true
    rm -f -- "/etc/systemd/system/$CONTROL_UNIT" "$ETC_DIR/control.env" \
        "$ETC_DIR/control-ca.pfx" "$ETC_DIR/control-server.pfx" \
        "$POLICY_HELPER" "$SUDOERS_FILE"
    rm -rf -- "$LIB_DIR/control" "$STATE_DIR/control" \
        "$STATE_DIR/control.db" "$STATE_DIR/control.db-wal" \
        "$STATE_DIR/control.db-shm" "$STATE_DIR/backups"
    remove_shared_ca_if_unused
    systemctl daemon-reload
    log "Control role and its state were removed."
}

preflight() {
    validate_platform
    local command_name
    for command_name in openssl sha256sum tar systemctl getent useradd groupadd; do
        require_command "$command_name"
    done
    log "Supported platform: $(. /etc/os-release; printf '%s %s' "$ID" "$VERSION_ID"), $(uname -m)"
}

main() {
    local action="${1:-help}"
    shift || true
    case "$action" in
        help|-h|--help) [[ $# -eq 0 ]] || fail "$action takes no arguments"; usage ;;
        version|--version) [[ $# -eq 0 ]] || fail "$action takes no arguments"; printf '%s %s\n' "$PROGRAM" "$PROGRAM_VERSION" ;;
        preflight) [[ $# -eq 0 ]] || fail "preflight takes no arguments"; preflight ;;
        verify-release) [[ $# -eq 1 ]] || fail "verify-release requires ARCHIVE"; verify_release_payload "$1" ;;
        install-control) [[ $# -ge 2 && $# -le 3 ]] || fail "install-control requires ARCHIVE PUBLIC_HOST [HTTPS_PORT]"; install_control "$@" ;;
        install-agent) [[ $# -eq 4 ]] || fail "install-agent requires ARCHIVE NODE_ID CONTROL_URL CA_CERT"; install_agent "$@" ;;
        install-node) [[ $# -eq 1 ]] || fail "install-node requires ARCHIVE"; install_node_from_code "$1" ;;
        verify-manifest) [[ $# -eq 2 ]] || fail "verify-manifest requires MANIFEST SIGNATURE"; verify_manifest "$1" "$2" ;;
        install-monitor) [[ $# -eq 1 ]] || fail "install-monitor requires PUBLIC_KEY"; install_monitor "$1" ;;
        uninstall-monitor) [[ $# -eq 0 ]] || fail "uninstall-monitor takes no arguments"; uninstall_monitor ;;
        mesh-init) [[ $# -ge 1 && $# -le 2 ]] || fail "mesh-init requires PUBLIC_ENDPOINT [WG_PORT]"; mesh_init "$@" ;;
        peer-add) [[ $# -eq 1 ]] || fail "peer-add requires SMMPEER1_CODE"; add_mesh_peer "$1" ;;
        mesh-status) [[ $# -eq 0 ]] || fail "mesh-status takes no arguments"; show_mesh_status ;;
        update-control) [[ $# -eq 1 ]] || fail "update-control requires ARCHIVE"; update_role control "$1" ;;
        update-agent) [[ $# -eq 1 ]] || fail "update-agent requires ARCHIVE"; update_role agent "$1" ;;
        rollback) [[ $# -ge 1 && $# -le 2 ]] || fail "rollback requires control|agent [BACKUP_ID]"; rollback_role "$@" ;;
        node-code) [[ $# -eq 1 ]] || fail "node-code requires NODE_ID"; create_node_code "$1" ;;
        control-device-code) [[ $# -eq 1 ]] || fail "control-device-code requires DEVICE_ID"; create_device_code "$1" ;;
        node-token) [[ $# -eq 1 ]] || fail "node-token requires NODE_ID"; run_control_cli token-create "$1" ;;
        control-ca-fingerprint) [[ $# -eq 0 ]] || fail "control-ca-fingerprint takes no arguments"; show_ca_fingerprint ;;
        status) [[ $# -eq 0 ]] || fail "status takes no arguments"; show_status ;;
        uninstall-agent) [[ $# -eq 0 || ( $# -eq 1 && "$1" == "--purge" ) ]] || fail "uninstall-agent accepts only [--purge]"; uninstall_agent "${1:-}" ;;
        uninstall-control) [[ $# -eq 1 ]] || fail "uninstall-control requires confirmation"; uninstall_control "$1" ;;
        *) fail "Unknown action: $action (run with --help)" ;;
    esac
}

main "$@"
