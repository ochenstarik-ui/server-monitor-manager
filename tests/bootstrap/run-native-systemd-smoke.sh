#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

archive="${1:?usage: run-native-systemd-smoke.sh ARCHIVE BOOTSTRAP}"
bootstrap="${2:?usage: run-native-systemd-smoke.sh ARCHIVE BOOTSTRAP}"
port="${SMM_SMOKE_PORT:-17443}"
system_bootstrap="/usr/local/sbin/ochenstarik-server-monitor-manager.sh"
probe_dir=""

# If manifest+sig are not shipped alongside the archive (CI-only builds),
# allow unsigned verification via .sha256 fallback.
archive_dir="$(dirname "$archive")"
if [[ ! -f "$archive_dir/server-monitor-manager-manifest.json" || ! -f "$archive_dir/server-monitor-manager-manifest.sig" ]]; then
    export SMM_ALLOW_UNSIGNED=1
fi

cleanup() {
    if [[ -n "$probe_dir" ]]; then
        rm -rf -- "$probe_dir"
    fi
    sudo "$system_bootstrap" uninstall-agent --purge >/dev/null 2>&1 || true
    sudo "$system_bootstrap" uninstall-control --confirm-destroy-control >/dev/null 2>&1 || true
}
trap cleanup EXIT

sudo "$bootstrap" preflight
sudo --preserve-env=SMM_ALLOW_UNSIGNED "$bootstrap" verify-release "$archive"
sudo --preserve-env=SMM_ALLOW_UNSIGNED "$bootstrap" install-control "$archive" 127.0.0.1 "$port"
sudo test -x "$system_bootstrap"
sudo test -x /usr/local/sbin/ochenstarik-smm-emergency
sudo /usr/local/sbin/ochenstarik-smm-emergency status

for _ in {1..30}; do
    if sudo curl --fail --silent \
        --cacert /etc/ochenstarik-server-monitor-manager/control-ca.crt \
        "https://127.0.0.1:$port/healthz" >/dev/null; then
        break
    fi
    sleep 1
done
sudo curl --fail --silent --show-error \
    --cacert /etc/ochenstarik-server-monitor-manager/control-ca.crt \
    "https://127.0.0.1:$port/healthz"

probe_dir="$(mktemp -d -t smm-agent-probe.XXXXXXXX)"
tar -xzf "$archive" -C "$probe_dir" agent
set +e
probe_output="$(env SMM_NodeId=INVALID_UPPER \
    "$probe_dir/agent/ochenstarik-smm-agent" 2>&1)"
probe_status=$?
set -e
[[ "$probe_status" -eq 2 ]] || {
    printf 'trimmed Agent invalid-environment probe returned %s, expected 2\n' "$probe_status" >&2
    exit 1
}
grep -Fq 'NodeId must contain lowercase letters, digits, or hyphens.' <<<"$probe_output"
if grep -Fq 'agent.pfx' <<<"$probe_output"; then
    printf '%s\n' 'trimmed Agent ignored SMM_NodeId and attempted to load agent.pfx' >&2
    exit 1
fi

set +e
probe_output="$(env SMM_NodeId=smoke-probe SMM_ControlUrl=http://127.0.0.1:1 \
    "$probe_dir/agent/ochenstarik-smm-agent" 2>&1)"
probe_status=$?
set -e
[[ "$probe_status" -eq 2 ]] || {
    printf 'trimmed Agent plaintext-ControlUrl probe returned %s, expected 2\n' "$probe_status" >&2
    exit 1
}
grep -Fq 'Agent ControlUrl must be a secure HTTPS origin.' <<<"$probe_output"
if grep -Eq 'agent\.pfx|Connection refused|127\.0\.0\.1' <<<"$probe_output"; then
    printf '%s\n' 'trimmed Agent plaintext-ControlUrl probe reached certificate or network access' >&2
    exit 1
fi
rm -rf -- "$probe_dir"
probe_dir=""

node_code="$(sudo "$system_bootstrap" node-code smoke-node)"
[[ "$node_code" == SMMNODE1.* || "$node_code" == SMMNODE2.* ]]
export SMM_ENROLL_CODE="$node_code"
export SMM_ACCEPT_CA_FINGERPRINT=1
sudo --preserve-env=SMM_ENROLL_CODE,SMM_ACCEPT_CA_FINGERPRINT --preserve-env=SMM_ALLOW_UNSIGNED \
    "$system_bootstrap" install-node "$archive"
unset SMM_ENROLL_CODE SMM_ACCEPT_CA_FINGERPRINT
node_code=""
sudo test -s /var/lib/ochenstarik-server-monitor-manager/agent/agent.pfx
sudo systemctl is-active --quiet ochenstarik-smm-agent.service
sudo systemctl is-active --quiet ochenstarik-smm-control.service

[[ "$(sudo stat -c '%a:%U:%G' /var/lib/ochenstarik-server-monitor-manager)" == '711:root:root' ]]
[[ "$(sudo stat -c '%a:%U:%G' /var/lib/ochenstarik-server-monitor-manager/control)" \
    == '700:ochenstarik-smm-control:ochenstarik-smm-control' ]]
[[ "$(sudo stat -c '%a:%U:%G' /var/lib/ochenstarik-server-monitor-manager/agent)" \
    == '700:ochenstarik-smm-agent:ochenstarik-smm-agent' ]]
if sudo -u ochenstarik-smm-agent test -r \
    /var/lib/ochenstarik-server-monitor-manager/control/control.db; then
    printf '%s\n' 'Agent can read the Control database' >&2
    exit 1
fi
if sudo -u ochenstarik-smm-agent id -nG | tr ' ' '\n' | \
    grep -Fxq ochenstarik-smm-control; then
    printf '%s\n' 'Agent unexpectedly belongs to the Control group' >&2
    exit 1
fi

device_code="$(sudo "$system_bootstrap" control-device-code smoke-device)"
[[ "$device_code" == SMMDEV1-* ]]
device_code=""

sudo --preserve-env=SMM_ALLOW_UNSIGNED "$system_bootstrap" update-control "$archive"
sudo systemctl is-active --quiet ochenstarik-smm-control.service
sudo systemctl is-active --quiet ochenstarik-smm-agent.service
sudo curl --fail --silent --show-error --retry 15 --retry-all-errors --retry-delay 1 \
    --cacert /etc/ochenstarik-server-monitor-manager/control-ca.crt \
    "https://127.0.0.1:$port/healthz"

printf '%s\n' "NATIVE_SYSTEMD_SMOKE=PASS"
