#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

archive="${1:?usage: run-native-systemd-smoke.sh ARCHIVE BOOTSTRAP}"
bootstrap="${2:?usage: run-native-systemd-smoke.sh ARCHIVE BOOTSTRAP}"
port="${SMM_SMOKE_PORT:-17443}"

cleanup() {
    sudo "$bootstrap" uninstall-control --confirm-destroy-control >/dev/null 2>&1 || true
}
trap cleanup EXIT

sudo "$bootstrap" preflight
sudo "$bootstrap" verify-release "$archive"
sudo "$bootstrap" install-control "$archive" 127.0.0.1 "$port"
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

sudo "$bootstrap" install-control "$archive" 127.0.0.1 "$port"
sudo systemctl restart ochenstarik-smm-control.service
sudo systemctl is-active --quiet ochenstarik-smm-control.service
sudo curl --fail --silent --show-error --retry 15 --retry-all-errors --retry-delay 1 \
    --cacert /etc/ochenstarik-server-monitor-manager/control-ca.crt \
    "https://127.0.0.1:$port/healthz"

printf '%s\n' "NATIVE_SYSTEMD_SMOKE=PASS"
