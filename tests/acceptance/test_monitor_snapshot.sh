#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
bootstrap="$root/deploy/ochenstarik-server-monitor-manager.sh"
contract="$root/tests/contracts/monitor-snapshot-v1.txt"
installer_contract="$root/docs/installer-contract.md"

grep -Fq 'Monitoring key permits only the exact versioned metrics snapshot listed below; no additional mesh status or other output is allowed.' "$installer_contract"
if grep -Fq 'read-only mesh status' "$installer_contract"; then
    printf '%s\n' 'installer contract still permits mesh status outside the closed snapshot' >&2
    exit 1
fi

extract_metrics_script() {
    awk '
        /cat >"\$metrics_script" <<'"'"'EOF'"'"'/ { emitting = 1; next }
        emitting && $0 == "EOF" { exit }
        emitting { print }
    ' "$bootstrap"
}

fixture="$(mktemp -d -t smm-monitor-snapshot.XXXXXXXX)"
trap 'rm -rf -- "$fixture"' EXIT
metrics_script="$fixture/ochenstarik-smm-metrics"
extract_metrics_script >"$metrics_script"
chmod 0755 "$metrics_script"

[[ -s "$metrics_script" ]] || {
    printf '%s\n' 'monitor metrics script was not found in bootstrap' >&2
    exit 1
}

metrics_out="$(bash "$metrics_script")"
printf '%s\n' "$metrics_out"

if grep -Ev '^[A-Z][A-Z0-9_]*=.*$' <<<"$metrics_out"; then
    printf '%s\n' 'monitor snapshot contains non-contract output' >&2
    exit 1
fi

expected_keys="$(cut -d= -f1 "$contract" | LC_ALL=C sort)"
actual_keys="$(cut -d= -f1 <<<"$metrics_out" | LC_ALL=C sort)"
if [[ "$actual_keys" != "$expected_keys" ]]; then
    printf '%s\n' 'monitor snapshot field closure differs from canonical contract' >&2
    diff -u <(printf '%s\n' "$expected_keys") <(printf '%s\n' "$actual_keys") >&2 || true
    exit 1
fi

[[ "$(grep -c '^PROTOCOL=1$' <<<"$metrics_out")" -eq 1 ]]
[[ "$(wc -l <"$contract")" -eq "$(wc -l <<<"$metrics_out")" ]]

printf '%s\n' 'MONITOR_SNAPSHOT_CONTRACT=PASS'
