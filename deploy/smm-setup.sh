#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

readonly PROGRAM_NAME="smm-setup"
readonly DEFAULT_RELEASE_TAG="v0.1.0-alpha.18"
readonly DEFAULT_REPOSITORY="ochenstarik-ui/server-monitor-manager"
readonly INNER_ASSET="ochenstarik-server-monitor-manager.sh"

RELEASE_TAG="${SMM_TAG:-$DEFAULT_RELEASE_TAG}"
REPOSITORY="${SMM_REPOSITORY:-$DEFAULT_REPOSITORY}"
CACHE_DIR="${SMM_CACHE_DIR:-${XDG_CACHE_HOME:-$HOME/.cache}/server-monitor-manager}"
interactive=0
node_code=""
node_control_url=""
node_hub_endpoint=""
node_ca_fingerprint=""

usage() {
    cat <<'USAGE'
Usage:
  smm-setup.sh
  smm-setup.sh [--tag TAG] [--repository OWNER/REPO] COMMAND [ARG...]

With no arguments, an interactive terminal guides a complete Hub or Node
installation. Non-interactive commands remain available:
  install-hub PUBLIC_HOST [HTTPS_PORT] [WG_PORT]
  install-node

Other commands are passed to the verified ochenstarik-server-monitor-manager.sh
asset. Use -- before a command to force pass-through. Common commands:
  install-agent | install-control | uninstall-agent | uninstall-control
  backup-create | backup-restore | version

Environment overrides:
  SMM_TAG         Release tag (default: v0.1.0-alpha.18)
  SMM_REPOSITORY  GitHub repository (default: ochenstarik-ui/server-monitor-manager)
  SMM_CACHE_DIR   Verified-download cache directory
USAGE
}

no_tty_help() {
    cat >&2 <<'HELP'
smm-setup: interactive installation requires a terminal on stdin and stdout.
For automation, choose an explicit command:
  sudo ./smm-setup.sh install-hub PUBLIC_HOST [HTTPS_PORT] [WG_PORT]
  sudo ./smm-setup.sh install-node
Run ./smm-setup.sh --help for pass-through commands.
HELP
}

die() { printf '%s: %s\n' "$PROGRAM_NAME" "$*" >&2; exit 1; }
require_command() { command -v "$1" >/dev/null 2>&1 || die "required command is unavailable: $1"; }

validate_port() {
    [[ "$1" =~ ^[0-9]{1,5}$ ]] && (( 10#$1 >= 1 && 10#$1 <= 65535 )) \
        || die "invalid port: $1"
}

validate_public_host() {
    [[ "$1" != *:* && "$1" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$ ]] \
        || die "invalid public address: $1"
}

prompt_default() {
    local prompt="$1" default="$2" value
    read -r -p "$prompt [$default]: " value
    printf '%s' "${value:-$default}"
}

confirm() {
    local prompt="$1" answer
    read -r -p "$prompt [y/N]: " answer
    [[ "$answer" == "y" || "$answer" == "Y" || "$answer" == "yes" || "$answer" == "YES" ]]
}

os_value() {
    local key="$1"
    [[ -r /etc/os-release ]] || return 0
    sed -n "s/^${key}=//p" /etc/os-release | head -n1 | sed 's/^"//;s/"$//'
}

detect_public_address() {
    command -v curl >/dev/null 2>&1 || return 0
    curl -fsS --max-time 5 https://api.ipify.org 2>/dev/null || true
}

role_status() {
    local role="$1" binary
    case "$role" in
        Hub) binary=/usr/local/lib/ochenstarik-server-monitor-manager/control/ochenstarik-smm-control ;;
        Node) binary=/usr/local/lib/ochenstarik-server-monitor-manager/agent/ochenstarik-smm-agent ;;
    esac
    [[ -x "$binary" ]] && printf 'installed' || printf 'not installed'
}

show_machine() {
    local distro version architecture public_address
    distro="$(os_value NAME)"
    version="$(os_value VERSION_ID)"
    architecture="$(uname -m)"
    public_address="$(detect_public_address)"
    printf '\nServer Monitor Manager setup\n'
    printf '  System: %s %s\n' "${distro:-unknown Linux}" "${version:-unknown}"
    printf '  Architecture: %s\n' "$architecture"
    printf '  Public address: %s\n' "${public_address:-not detected}"
    printf '  Hub: %s\n' "$(role_status Hub)"
    printf '  Node: %s\n\n' "$(role_status Node)"
    DETECTED_PUBLIC_ADDRESS="$public_address"
}

install_dependencies() {
    local item missing=()
    for item in curl sha256sum mktemp openssl base64 install; do
        command -v "$item" >/dev/null 2>&1 || missing+=("$item")
    done
    if (( ${#missing[@]} > 0 )); then
        [[ ${EUID:-$(id -u)} -eq 0 ]] || die "run with sudo to install dependencies: ${missing[*]}"
        command -v apt-get >/dev/null 2>&1 || die "install required commands manually: ${missing[*]}"
        printf 'Installing required system packages...\n'
        DEBIAN_FRONTEND=noninteractive apt-get update
        DEBIAN_FRONTEND=noninteractive apt-get install -y ca-certificates curl coreutils openssl
    fi
    printf 'Dependencies are ready. The verified bootstrap will provision cosign if needed.\n'
}

base64url_decode() {
    local encoded="$1" padding
    [[ -n "$encoded" && "$encoded" =~ ^[A-Za-z0-9_-]+$ ]] || return 1
    encoded="${encoded//-/+}"
    encoded="${encoded//_/\/}"
    case $(( ${#encoded} % 4 )) in
        0) padding="" ;; 2) padding="==" ;; 3) padding="=" ;; *) return 1 ;;
    esac
    printf '%s%s' "$encoded" "$padding" | base64 --decode 2>/dev/null
}

inspect_node_code() {
    local code="$1" parts=() ca_file hub_key node_address mesh_network part
    local control_url_pattern='^https://(\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9.-]+)(:[0-9]{1,5})?/?$'
    IFS='.' read -r -a parts <<<"$code"
    [[ ${#parts[@]} -eq 9 && "${parts[0]}" == "SMMNODE2" ]] \
        || die "invalid SMMNODE2 code: expected exactly nine segments"
    for part in "${parts[@]:1}"; do [[ -n "$part" ]] || die "invalid SMMNODE2 code: empty segment"; done
    node_control_url="$(base64url_decode "${parts[1]}")" || die "invalid SMMNODE2 Control URL encoding"
    node_hub_endpoint="$(base64url_decode "${parts[5]}")" || die "invalid SMMNODE2 Hub endpoint encoding"
    hub_key="$(base64url_decode "${parts[6]}")" || die "invalid SMMNODE2 Hub key encoding"
    node_address="$(base64url_decode "${parts[7]}")" || die "invalid SMMNODE2 node address encoding"
    mesh_network="$(base64url_decode "${parts[8]}")" || die "invalid SMMNODE2 network encoding"
    [[ "$node_control_url" =~ $control_url_pattern ]] || die "invalid Control URL in SMMNODE2 code"
    [[ "$node_hub_endpoint" =~ ^[A-Za-z0-9.-]+:[0-9]{1,5}$ ]] || die "invalid Hub endpoint in SMMNODE2 code"
    validate_port "${node_hub_endpoint##*:}"
    [[ "$hub_key" =~ ^[A-Za-z0-9+/]{43}=$ ]] || die "invalid Hub public key in SMMNODE2 code"
    [[ "$node_address" =~ ^10\.77\.0\.([2-9]|[1-9][0-9]|1[0-9]{2}|2[0-4][0-9]|25[0-4])$ ]] \
        || die "invalid mesh address in SMMNODE2 code"
    [[ "$mesh_network" == "10.77.0.0/24" ]] || die "invalid mesh network in SMMNODE2 code"
    ca_file="$(mktemp -t smm-setup-ca.XXXXXXXX.crt)"
    chmod 0600 "$ca_file"
    if ! base64url_decode "${parts[2]}" >"$ca_file" \
        || ! openssl x509 -in "$ca_file" -noout >/dev/null 2>&1; then
        rm -f -- "$ca_file"
        die "invalid or tampered CA certificate in SMMNODE2 code"
    fi
    node_ca_fingerprint="$(openssl x509 -in "$ca_file" -noout -fingerprint -sha256 | cut -d= -f2)"
    rm -f -- "$ca_file"
}

choose_interactive_action() {
    local role host https_port wg_port existing default_host
    show_machine
    read -r -p 'Choose role: 1) Hub  2) Node: ' role
    case "${role,,}" in
        1|hub)
            action=install-hub; existing="$(role_status Hub)"
            default_host="${DETECTED_PUBLIC_ADDRESS:-hub.example.com}"
            [[ -n "$default_host" ]] || default_host=hub.example.com
            host="$(prompt_default 'Public IPv4 address or DNS name' "$default_host")"
            https_port="$(prompt_default 'Control HTTPS port' 7443)"
            wg_port="$(prompt_default 'WireGuard UDP port' 51820)"
            validate_public_host "$host"; validate_port "$https_port"; validate_port "$wg_port"
            action_args=("$host" "$https_port" "$wg_port")
            ;;
        2|node)
            action=install-node; existing="$(role_status Node)"
            read -r -s -p 'Paste SMMNODE2 code: ' node_code; printf '\n'
            [[ -n "$node_code" ]] || die "SMMNODE2 code is empty"
            action_args=()
            ;;
        *) die "choose Hub (1) or Node (2)" ;;
    esac
    if [[ "$existing" == "installed" ]] && ! confirm "This role is already installed. Reinstall or update it?"; then
        printf 'No changes were made.\n'; exit 0
    fi
    install_dependencies
    if [[ "$action" == install-node ]]; then
        inspect_node_code "$node_code"
        printf '\nNode enrollment details:\n  Control: %s\n  WireGuard Hub: %s\n  CA SHA-256: %s\n' \
            "$node_control_url" "$node_hub_endpoint" "$node_ca_fingerprint"
        confirm 'Do these Hub and CA fingerprint values match the operator application?' \
            || die "installation cancelled: Hub identity was not confirmed"
    fi
}

original_count=$#
pass_through=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --tag) [[ $# -ge 2 ]] || die '--tag requires a value'; RELEASE_TAG="$2"; shift 2 ;;
        --repository) [[ $# -ge 2 ]] || die '--repository requires a value'; REPOSITORY="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        --) pass_through=1; shift; break ;;
        -*) die "unknown option: $1" ;;
        *) break ;;
    esac
done

action_args=()
action=""
if (( original_count == 0 )); then
    [[ -t 0 && -t 1 ]] || { no_tty_help; exit 2; }
    interactive=1
    choose_interactive_action
else
    [[ $# -gt 0 ]] || die "a command is required after options"
    action="$1"; shift; action_args=("$@")
fi

[[ "$RELEASE_TAG" =~ ^v[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$ ]] || die "invalid release tag: $RELEASE_TAG"
[[ "$REPOSITORY" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || die "invalid repository: $REPOSITORY"

if (( pass_through == 0 )); then
    case "$action" in
        install-hub) [[ ${#action_args[@]} -ge 1 && ${#action_args[@]} -le 3 ]] || die "install-hub requires PUBLIC_HOST [HTTPS_PORT] [WG_PORT]" ;;
        install-node) [[ ${#action_args[@]} -eq 0 ]] || die "install-node takes no arguments" ;;
    esac
fi

require_command curl; require_command sha256sum; require_command mktemp
release_base="https://github.com/$REPOSITORY/releases/download/$RELEASE_TAG"
cache_release="$CACHE_DIR/$REPOSITORY/$RELEASE_TAG"
cached_script="$cache_release/$INNER_ASSET"
cached_checksum="$cache_release/$INNER_ASSET.sha256"
mkdir -p "$cache_release"
temporary_directory="$(mktemp -d -t smm-setup.XXXXXXXX)"
trap 'rm -rf -- "$temporary_directory"' EXIT

download() {
    local url="$1" destination="$2"
    if (( interactive == 1 )); then
        printf 'Downloading %s\n' "${url##*/}"
        curl -fL --progress-bar "$url" -o "$destination"
    else
        curl -fsSL "$url" -o "$destination"
    fi
}

download "$release_base/$INNER_ASSET" "$temporary_directory/$INNER_ASSET"
download "$release_base/$INNER_ASSET.sha256" "$temporary_directory/$INNER_ASSET.sha256"
( cd "$temporary_directory"; sha256sum -c "$INNER_ASSET.sha256" >/dev/null ) \
    || die "checksum verification failed for $RELEASE_TAG/$INNER_ASSET"
install -m 0755 "$temporary_directory/$INNER_ASSET" "$cached_script"
install -m 0644 "$temporary_directory/$INNER_ASSET.sha256" "$cached_checksum"

download_required_asset() {
    local asset="$1"
    if ! download "$release_base/$asset" "$temporary_directory/$asset"; then
        case "$asset" in
            server-monitor-manager-manifest.json|server-monitor-manager-manifest.sig|server-monitor-manager-manifest.pem)
                die "required signed-release asset is unavailable: $asset"
                ;;
            *) die "required release asset is unavailable: $asset" ;;
        esac
    fi
}

download_platform_release() {
    local platform archive_asset asset
    case "$(uname -m)" in x86_64) platform="linux-x64" ;; aarch64|arm64) platform="linux-arm64" ;; *) die "unsupported architecture: $(uname -m)" ;; esac
    archive_asset="server-monitor-manager-$platform.tar.gz"
    for asset in "$archive_asset" "$archive_asset.sha256" server-monitor-manager-manifest.json \
        server-monitor-manager-manifest.sig server-monitor-manager-manifest.pem; do
        download_required_asset "$asset"
    done
    ( cd "$temporary_directory"; sha256sum -c "$archive_asset.sha256" >/dev/null ) \
        || die "checksum verification failed for $RELEASE_TAG/$archive_asset"
    downloaded_archive="$temporary_directory/$archive_asset"
}

if (( pass_through == 1 )); then exec "$cached_script" "$action" "${action_args[@]}"; fi

case "$action" in
    install-hub)
        download_platform_release; archive="$downloaded_archive"
        public_host="${action_args[0]}"; https_port="${action_args[1]:-}"; wg_port="${action_args[2]:-}"
        if [[ -n "$https_port" ]]; then "$cached_script" install-control "$archive" "$public_host" "$https_port"; else "$cached_script" install-control "$archive" "$public_host"; fi
        if [[ -n "$wg_port" ]]; then "$cached_script" mesh-init "$public_host" "$wg_port"; else "$cached_script" mesh-init "$public_host"; fi
        if (( interactive == 1 )); then
            printf '\nHub installation is complete. Insert this device registration code into the operator application:\n'
            "$cached_script" control-device-code operator
            printf 'Hub CA SHA-256 fingerprint (verify it in the application):\n'
            "$cached_script" control-ca-fingerprint
        fi
        ;;
    install-node)
        download_platform_release; archive="$downloaded_archive"
        if (( interactive == 1 )); then
            SMM_ENROLL_CODE="$node_code" SMM_ACCEPT_CA_FINGERPRINT=1 "$cached_script" install-node "$archive"
            node_code=""
            printf '\nNode installation is complete. Insert the SMMPEER1 code printed above into the operator application for this Node.\n'
        else
            exec "$cached_script" install-node "$archive"
        fi
        ;;
    *) exec "$cached_script" "$action" "${action_args[@]}" ;;
esac
