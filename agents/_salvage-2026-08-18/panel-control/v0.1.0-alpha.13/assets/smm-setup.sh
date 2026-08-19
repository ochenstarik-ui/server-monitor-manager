#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

readonly PROGRAM_NAME="smm-setup"
readonly DEFAULT_RELEASE_TAG="v0.1.0-alpha.13"
readonly DEFAULT_REPOSITORY="ochenstarik-ui/server-monitor-manager"
readonly INNER_ASSET="ochenstarik-server-monitor-manager.sh"

RELEASE_TAG="${SMM_TAG:-$DEFAULT_RELEASE_TAG}"
REPOSITORY="${SMM_REPOSITORY:-$DEFAULT_REPOSITORY}"
CACHE_DIR="${SMM_CACHE_DIR:-${XDG_CACHE_HOME:-$HOME/.cache}/server-monitor-manager}"

usage() {
    cat <<'USAGE'
Usage:
  smm-setup.sh [--tag TAG] [--repository OWNER/REPO] COMMAND [ARG...]

Commands are passed to the verified ochenstarik-server-monitor-manager.sh
asset from the selected immutable GitHub release. Common commands:
  install-agent | install-control | uninstall-agent | uninstall-control
  backup-create | backup-restore | version

Environment overrides:
  SMM_TAG         Release tag (default: v0.1.0-alpha.13)
  SMM_REPOSITORY  GitHub repository (default: ochenstarik-ui/server-monitor-manager)
  SMM_CACHE_DIR   Verified-download cache directory
USAGE
}

die() {
    printf '%s: %s\n' "$PROGRAM_NAME" "$*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || die "required command is unavailable: $1"
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --tag)
            [[ $# -ge 2 ]] || die '--tag requires a value'
            RELEASE_TAG="$2"
            shift 2
            ;;
        --repository)
            [[ $# -ge 2 ]] || die '--repository requires a value'
            REPOSITORY="$2"
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        --)
            shift
            break
            ;;
        -*)
            die "unknown option: $1"
            ;;
        *)
            break
            ;;
    esac
done

[[ $# -gt 0 ]] || {
    usage >&2
    exit 2
}
[[ "$RELEASE_TAG" =~ ^v[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$ ]] \
    || die "invalid release tag: $RELEASE_TAG"
[[ "$REPOSITORY" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] \
    || die "invalid repository: $REPOSITORY"

require_command curl
require_command sha256sum
require_command mktemp

release_base="https://github.com/$REPOSITORY/releases/download/$RELEASE_TAG"
cache_release="$CACHE_DIR/$REPOSITORY/$RELEASE_TAG"
cached_script="$cache_release/$INNER_ASSET"
cached_checksum="$cache_release/$INNER_ASSET.sha256"
mkdir -p "$cache_release"

temporary_directory="$(mktemp -d -t smm-setup.XXXXXXXX)"
trap 'rm -rf -- "$temporary_directory"' EXIT

curl -fsSL "$release_base/$INNER_ASSET" -o "$temporary_directory/$INNER_ASSET"
curl -fsSL "$release_base/$INNER_ASSET.sha256" -o "$temporary_directory/$INNER_ASSET.sha256"

(
    cd "$temporary_directory"
    sha256sum -c "$INNER_ASSET.sha256"
) || die "checksum verification failed for $RELEASE_TAG/$INNER_ASSET"

install -m 0755 "$temporary_directory/$INNER_ASSET" "$cached_script"
install -m 0644 "$temporary_directory/$INNER_ASSET.sha256" "$cached_checksum"

exec "$cached_script" "$@"
