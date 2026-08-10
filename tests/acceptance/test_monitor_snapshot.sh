#!/usr/bin/env bash
set -euo pipefail

# test_monitor_snapshot.sh
# Acceptance test for Monitor role installation and metrics format

cd "$(dirname "$0")/../.."
BOOTSTRAP="./deploy/ochenstarik-server-monitor-manager.sh"
DUMMY_KEY="ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIDummyTestKeyForAcceptanceTest dummy@test"

echo "=== Testing install-monitor ==="
sudo "$BOOTSTRAP" install-monitor "$DUMMY_KEY"

# 1. Verify user exists and has nologin
getent passwd ochenstarik-monitor | grep -q "/usr/sbin/nologin" || {
    echo "ERROR: User ochenstarik-monitor does not exist or does not have nologin"
    exit 1
}

# 2. Verify authorized_keys
AUTH_KEYS="/var/lib/ochenstarik-monitor/.ssh/authorized_keys"
if ! sudo test -f "$AUTH_KEYS"; then
    echo "ERROR: authorized_keys not found"
    exit 1
fi

sudo grep -q "command=\"/usr/local/libexec/ochenstarik-smm-metrics\",restrict" "$AUTH_KEYS" || {
    echo "ERROR: Forced command not found in authorized_keys"
    exit 1
}

# 3. Verify metrics script output format
echo "=== Testing metrics output ==="
METRICS_OUT=$(sudo /usr/local/libexec/ochenstarik-smm-metrics)
echo "$METRICS_OUT"

for FIELD in PROTOCOL HOSTNAME UPTIME_SECONDS LOAD1 CPU_COUNT MEM_TOTAL_KB MEM_AVAILABLE_KB SWAP_TOTAL_KB SWAP_FREE_KB DISK_TOTAL_KB DISK_AVAILABLE_KB DISK_INODES_TOTAL DISK_INODES_FREE NETWORK_RX_BYTES NETWORK_TX_BYTES KERNEL; do
    if ! echo "$METRICS_OUT" | grep -q "^${FIELD}="; then
        echo "ERROR: Missing field ${FIELD} in metrics output"
        exit 1
    fi
done

# 4. Verify uninstall
echo "=== Testing uninstall-monitor ==="
sudo "$BOOTSTRAP" uninstall-monitor

if getent passwd ochenstarik-monitor >/dev/null 2>&1; then
    echo "ERROR: User ochenstarik-monitor was not removed"
    exit 1
fi

if sudo test -f "$AUTH_KEYS" || sudo test -f "/usr/local/libexec/ochenstarik-smm-metrics"; then
    echo "ERROR: Leftover files found after uninstall"
    exit 1
fi

echo "=== PASS: Monitor role acceptance test ==="
