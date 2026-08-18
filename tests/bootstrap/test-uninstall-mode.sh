#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
bootstrap="$root/deploy/ochenstarik-server-monitor-manager.sh"
setup="$root/deploy/smm-setup.sh"
refusal_output="$(mktemp -t smm-uninstall-refused.XXXXXXXX)"
trap 'rm -f -- "$refusal_output"' EXIT

bash -n "$bootstrap" "$setup"

if bash "$bootstrap" uninstall-system >/dev/null 2>"$refusal_output"; then
    printf '%s\n' 'uninstall-system accepted a request without confirmation' >&2
    exit 1
fi
grep -Fq -- '--confirm-uninstall' "$refusal_output"

definition="$(awk '
    /^uninstall_system\(\) \{/ { active=1 }
    active { print }
    active && /^}$/ { exit }
' "$bootstrap")"
stop_line="$(grep -Fn '    stop_owned_services' <<<"$definition" | cut -d: -f1)"
term_line="$(grep -Fn '    terminate_owned_processes' <<<"$definition" | cut -d: -f1)"
network_line="$(grep -Fn '    remove_owned_network' <<<"$definition" | cut -d: -f1)"
files_line="$(grep -Fn '    remove_owned_files' <<<"$definition" | cut -d: -f1)"
accounts_line="$(grep -Fn '    remove_owned_accounts' <<<"$definition" | cut -d: -f1)"
(( stop_line < term_line && term_line < network_line && network_line < files_line && files_line < accounts_line ))

grep -Fq 'pkill -TERM -u "$user"' "$bootstrap"
grep -Fq 'pkill -KILL -u "$user"' "$bootstrap"
grep -Fq 'nft delete table inet ochenstarik_smm' "$bootstrap"
grep -Fq 'ip link delete smm0' "$bootstrap"
grep -Fq 'port-7443:' "$bootstrap"
grep -Fq 'port-51820:' "$bootstrap"
grep -Fq 'mesh-routes:' "$bootstrap"
grep -Fq 'owned-units:' "$bootstrap"
grep -Fq 'owned-users:' "$bootstrap"
grep -Fq 'control-ca.pfx' "$bootstrap"
grep -Fq 'bootstrap-backups' "$bootstrap"
grep -Fq 'Type UNINSTALL' "$setup"
grep -Fq 'Type DESTROY-DATA' "$setup"
grep -Fq '3) Uninstall' "$setup"

if grep -Eiq '3x-ui|x-ui' "$bootstrap" "$setup"; then
    printf '%s\n' 'uninstall implementation references protected 3x-ui objects' >&2
    exit 1
fi
if grep -Eq "rm -rf --? [\"']?/(etc|var|usr)(/|[\"']|$)" "$bootstrap"; then
    printf '%s\n' 'uninstall implementation contains a broad recursive deletion' >&2
    exit 1
fi

printf '%s\n' 'UNINSTALL_MODE=PASS'
