#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

readonly PROGRAM="ochenstarik-server-monitor-manager"
readonly PROGRAM_VERSION="0.2.0-dev"
readonly ETC_DIR="/etc/ochenstarik-server-monitor-manager"
readonly LIB_DIR="/usr/local/lib/ochenstarik-server-monitor-manager"
readonly STATE_DIR="/var/lib/ochenstarik-server-monitor-manager"
readonly BACKUP_DIR="${STATE_DIR}/bootstrap-backups"
readonly CONTROL_USER="ochenstarik-smm-control"
readonly AGENT_USER="ochenstarik-smm-agent"
readonly CONTROL_UNIT="ochenstarik-smm-control.service"
readonly AGENT_UNIT="ochenstarik-smm-agent.service"
readonly POLICY_HELPER="/usr/local/libexec/ochenstarik-smm-policy-apply"
readonly SUDOERS_FILE="/etc/sudoers.d/ochenstarik-smm-control"
readonly MESH_DIR="${STATE_DIR}/mesh"
readonly WG_DIR="${ETC_DIR}/wireguard"
readonly FIREWALL_UNIT="ochenstarik-smm-firewall.service"
readonly MESH_NETWORK="10.77.0.0/24"
readonly HUB_MESH_ADDRESS="10.77.0.1/24"

TEMP_DIR=""
MESH_PEER_CODE=""

log() { printf '%s\n' "[$PROGRAM] $*"; }
fail() { printf '%s\n' "[$PROGRAM] ERROR: $*" >&2; exit 1; }

cleanup() {
    if [[ -n "$TEMP_DIR" && -d "$TEMP_DIR" ]]; then
        rm -rf -- "$TEMP_DIR"
    fi
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
  ochenstarik-server-monitor-manager.sh mesh-init PUBLIC_ENDPOINT [WG_PORT]
  ochenstarik-server-monitor-manager.sh peer-add SMMPEER1_CODE
  ochenstarik-server-monitor-manager.sh mesh-status
  ochenstarik-server-monitor-manager.sh update-control ARCHIVE
  ochenstarik-server-monitor-manager.sh update-agent ARCHIVE
  ochenstarik-server-monitor-manager.sh rollback control|agent [BACKUP_ID]
  ochenstarik-server-monitor-manager.sh node-code NODE_ID
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
    [[ "$1" =~ ^[0-9]+$ ]] && (( 10#$1 >= 1 && 10#$1 <= 65535 )) \
        || fail "Port must be in range 1-65535."
}

validate_control_url() {
    [[ "$1" =~ ^https://[A-Za-z0-9._:\[\]-]+(:[0-9]{1,5})?/?$ ]] \
        || fail "Control URL must be an https URL without a path or credentials."
}

verify_archive() {
    local archive="$1" checksum_file expected actual entry
    [[ -f "$archive" ]] || fail "Archive not found: $archive"
    checksum_file="${archive}.sha256"
    [[ -f "$checksum_file" ]] || fail "Checksum file not found: $checksum_file"
    expected="$(awk 'NR == 1 { print $1 }' "$checksum_file")"
    [[ "$expected" =~ ^[0-9a-fA-F]{64}$ ]] || fail "Invalid checksum file: $checksum_file"
    actual="$(sha256sum "$archive" | awk '{ print $1 }')"
    [[ "${actual,,}" == "${expected,,}" ]] || fail "Archive checksum mismatch."

    while IFS= read -r entry; do
        [[ -n "$entry" ]] || continue
        [[ "$entry" != /* && "$entry" != *".."* ]] || fail "Unsafe archive entry: $entry"
        case "$entry" in
            agent|agent/*|control|control/*|deploy|deploy/*|bootstrap|bootstrap/*) ;;
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
    [[ -f "$TEMP_DIR/deploy/$FIREWALL_UNIT" ]] || fail "Mesh firewall systemd unit is missing from archive."
}

verify_release_payload() {
    local archive="$1"
    require_command sha256sum
    require_command tar
    extract_archive "$archive"
    [[ -x "$TEMP_DIR/control/ochenstarik-smm-control" ]] || fail "Control binary is missing."
    [[ -x "$TEMP_DIR/agent/ochenstarik-smm-agent" ]] || fail "Agent binary is missing."
    [[ -x "$TEMP_DIR/deploy/ochenstarik-smm-policy-apply" ]] || fail "Policy helper is missing."
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
                "etc/ochenstarik-server-monitor-manager/agent.env"
                "etc/ochenstarik-server-monitor-manager/control-ca.crt"
                "etc/systemd/system/$AGENT_UNIT"
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
    install -d -m 0750 -o root -g "$CONTROL_USER" "$ETC_DIR"
    install -d -m 0750 -o "$CONTROL_USER" -g "$CONTROL_USER" "$STATE_DIR" "$STATE_DIR/backups"
    install_tree_atomic "$TEMP_DIR/control" "$LIB_DIR/control" "root:root"
    if [[ ! -f "$ETC_DIR/control-ca.pfx" || ! -f "$ETC_DIR/control-server.pfx" ]]; then
        create_control_certificates "$public_host"
    fi
    cat >"$ETC_DIR/control.env" <<EOF
ASPNETCORE_URLS=https://0.0.0.0:$port
ASPNETCORE_Kestrel__Certificates__Default__Path=$ETC_DIR/control-server.pfx
Control__DatabasePath=$STATE_DIR/control.db
Control__CertificateAuthorityPath=$ETC_DIR/control-ca.pfx
Control__BackupDirectory=$STATE_DIR/backups
Control__HubHelperPath=$POLICY_HELPER
Control__PrivilegeEscalationPath=/usr/bin/sudo
EOF
    printf '%s\n' "https://$public_host:$port" >"$ETC_DIR/control-public-url"
    chown root:"$CONTROL_USER" "$ETC_DIR/control.env"
    chmod 0640 "$ETC_DIR/control.env"
    chmod 0644 "$ETC_DIR/control-public-url"
    install -d -m 0755 "$(dirname "$POLICY_HELPER")"
    install -m 0755 "$TEMP_DIR/deploy/ochenstarik-smm-policy-apply" "$POLICY_HELPER"
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
    local archive="$1" node_id="$2" control_url="$3" ca_cert="$4" backup_id
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
    backup_id="$(create_backup agent)"
    ensure_system_user "$AGENT_USER"
    install -d -m 0750 -o root -g "$AGENT_USER" "$ETC_DIR"
    install -d -m 0700 -o "$AGENT_USER" -g "$AGENT_USER" "$STATE_DIR/agent"
    install_tree_atomic "$TEMP_DIR/agent" "$LIB_DIR/agent" "root:root"
    if [[ "$(realpath "$ca_cert")" != "$(realpath -m "$ETC_DIR/control-ca.crt")" ]]; then
        install -m 0600 -o "$AGENT_USER" -g "$AGENT_USER" "$ca_cert" "$ETC_DIR/control-ca.crt"
    else
        chown "$AGENT_USER:$AGENT_USER" "$ETC_DIR/control-ca.crt"
        chmod 0600 "$ETC_DIR/control-ca.crt"
    fi
    cat >"$ETC_DIR/agent.env" <<EOF
SMM_NodeId=$node_id
SMM_ControlUrl=${control_url%/}
SMM_StateDirectory=$STATE_DIR/agent
SMM_CertificateAuthorityPath=$ETC_DIR/control-ca.crt
EOF
    chown root:"$AGENT_USER" "$ETC_DIR/agent.env"
    chmod 0640 "$ETC_DIR/agent.env"
    read_enrollment_token
    runuser -u "$AGENT_USER" -- env \
        "SMM_NodeId=$node_id" \
        "SMM_ControlUrl=${control_url%/}" \
        "SMM_StateDirectory=$STATE_DIR/agent" \
        "SMM_CertificateAuthorityPath=$ETC_DIR/control-ca.crt" \
        "SMM_EnrollToken=$ENROLL_TOKEN" \
        "$LIB_DIR/agent/ochenstarik-smm-agent"
    ENROLL_TOKEN=""
    chown root:"$AGENT_USER" "$ETC_DIR/control-ca.crt"
    chmod 0640 "$ETC_DIR/control-ca.crt"
    install_unit "$TEMP_DIR/deploy/$AGENT_UNIT" "$AGENT_UNIT"
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
    systemctl stop "$unit"
    backup_id="$(create_backup "$role")"
    install_tree_atomic "$TEMP_DIR/$role" "$LIB_DIR/$role" "$user"
    systemctl start "$unit"
    if ! systemctl is-active --quiet "$unit"; then
        log "Update failed; restoring backup $backup_id"
        restore_backup "$role" "$backup_id"
        fail "$role update was rolled back."
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
    tar -C / -xzf "$archive"
    systemctl daemon-reload
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

uninstall_agent() {
    local purge="${1:-}"
    require_root
    systemctl disable --now "$AGENT_UNIT" 2>/dev/null || true
    rm -f -- "/etc/systemd/system/$AGENT_UNIT" "$ETC_DIR/agent.env"
    rm -rf -- "$LIB_DIR/agent"
    [[ "$purge" == "--purge" ]] && rm -rf -- "$STATE_DIR/agent" "$ETC_DIR/control-ca.crt"
    systemctl daemon-reload
    log "Agent removed${purge:+ ($purge)}."
}

uninstall_control() {
    [[ "${1:-}" == "--confirm-destroy-control" ]] || fail "Control removal requires --confirm-destroy-control"
    require_root
    systemctl disable --now "$CONTROL_UNIT" 2>/dev/null || true
    rm -f -- "/etc/systemd/system/$CONTROL_UNIT" "$ETC_DIR/control.env" \
        "$ETC_DIR/control-ca.pfx" "$ETC_DIR/control-server.pfx" "$ETC_DIR/control-ca.crt" \
        "$POLICY_HELPER" "$SUDOERS_FILE"
    rm -rf -- "$LIB_DIR/control" "$STATE_DIR/control.db" "$STATE_DIR/control.db-wal" \
        "$STATE_DIR/control.db-shm" "$STATE_DIR/backups"
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
        help|-h|--help) usage ;;
        version|--version) printf '%s %s\n' "$PROGRAM" "$PROGRAM_VERSION" ;;
        preflight) preflight ;;
        verify-release) [[ $# -eq 1 ]] || fail "verify-release requires ARCHIVE"; verify_release_payload "$1" ;;
        install-control) [[ $# -ge 2 && $# -le 3 ]] || fail "install-control requires ARCHIVE PUBLIC_HOST [HTTPS_PORT]"; install_control "$@" ;;
        install-agent) [[ $# -eq 4 ]] || fail "install-agent requires ARCHIVE NODE_ID CONTROL_URL CA_CERT"; install_agent "$@" ;;
        install-node) [[ $# -eq 1 ]] || fail "install-node requires ARCHIVE"; install_node_from_code "$1" ;;
        mesh-init) [[ $# -ge 1 && $# -le 2 ]] || fail "mesh-init requires PUBLIC_ENDPOINT [WG_PORT]"; mesh_init "$@" ;;
        peer-add) [[ $# -eq 1 ]] || fail "peer-add requires SMMPEER1_CODE"; add_mesh_peer "$1" ;;
        mesh-status) [[ $# -eq 0 ]] || fail "mesh-status takes no arguments"; show_mesh_status ;;
        update-control) [[ $# -eq 1 ]] || fail "update-control requires ARCHIVE"; update_role control "$1" ;;
        update-agent) [[ $# -eq 1 ]] || fail "update-agent requires ARCHIVE"; update_role agent "$1" ;;
        rollback) [[ $# -ge 1 && $# -le 2 ]] || fail "rollback requires control|agent [BACKUP_ID]"; rollback_role "$@" ;;
        node-code) [[ $# -eq 1 ]] || fail "node-code requires NODE_ID"; create_node_code "$1" ;;
        node-token) [[ $# -eq 1 ]] || fail "node-token requires NODE_ID"; run_control_cli token-create "$1" ;;
        control-ca-fingerprint) [[ $# -eq 0 ]] || fail "control-ca-fingerprint takes no arguments"; show_ca_fingerprint ;;
        status) show_status ;;
        uninstall-agent) [[ $# -le 1 ]] || fail "uninstall-agent accepts only [--purge]"; uninstall_agent "${1:-}" ;;
        uninstall-control) [[ $# -eq 1 ]] || fail "uninstall-control requires confirmation"; uninstall_control "$1" ;;
        *) fail "Unknown action: $action (run with --help)" ;;
    esac
}

main "$@"
