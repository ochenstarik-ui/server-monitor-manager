#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

[[ $# -eq 1 ]] || {
    printf 'usage: %s CONTROL_BINARY\n' "$0" >&2
    exit 2
}

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
binary="$(realpath "$1")"
fixture="$root/tests/fixtures/control-v0.1.0-alpha.7.db"
work="$(mktemp -d -t smm-arm64-schema-compatibility.XXXXXXXX)"
trap 'rm -rf -- "$work"' EXIT

[[ -x "$binary" ]] || {
    printf 'published Control binary is not executable: %s\n' "$binary" >&2
    exit 1
}
cp "$fixture" "$work/control.db"
printf '%s' 'fixture-ca' >"$work/control-ca.pfx"
mkdir -p "$work/backups" "$work/bundle"

export Control__DatabasePath="$work/control.db"
export Control__CertificateAuthorityPath="$work/control-ca.pfx"
export Control__BackupDirectory="$work/backups"
export Control__HubHelperPath=/bin/true
export Control__PrivilegeEscalationPath=/usr/bin/true
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="$work/bundle"
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

"$binary" backup-create
backup_paths=("$work"/backups/backup-*)
[[ ${#backup_paths[@]} -eq 1 && -d "${backup_paths[0]}" ]] || {
    printf '%s\n' 'arm64 published Control did not create exactly one backup' >&2
    exit 1
}

rm -f -- "$work/control.db" "$work/control.db-wal" "$work/control.db-shm" "$work/control-ca.pfx"
"$binary" backup-restore "${backup_paths[0]}"
[[ -s "$work/control.db" && -s "$work/control-ca.pfx" ]]
"$binary" backup-create

printf '%s\n' 'PREVIOUS_SCHEMA_ARM64=PASS'
