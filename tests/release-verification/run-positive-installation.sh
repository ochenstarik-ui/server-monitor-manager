#!/bin/bash
set -euo pipefail

TAG="${1:-}"

if [[ -z "$TAG" ]]; then
    echo "Usage: $0 <tag>"
    exit 1
fi

echo "Running positive installation test for $TAG..."

# Fetch smm-setup.sh
gh release download "$TAG" -p 'smm-setup.sh*'

# Verify checksum
sha256sum -c smm-setup.sh.sha256

# The archive is downloaded by verify-release or we must download it?
# In smm-setup.sh, the owner manually downloads the archive?
# Wait, let's look at docs: "загрузка bootstrap и архива из релиза, проверка контрольных сумм, проверка подписи manifest"
# Actually, the user does:
ARCHIVE="server-monitor-manager-linux-$(uname -m | sed -e 's/x86_64/x64/' -e 's/aarch64/arm64/').tar.gz"
gh release download "$TAG" -p "$ARCHIVE*"
gh release download "$TAG" -p "server-monitor-manager-manifest.*"

sha256sum -c "$ARCHIVE.sha256"

# Run setup steps through smm-setup.sh
# "preflight, verify-release, установка Control, mesh-init"
sudo bash smm-setup.sh preflight
sudo bash smm-setup.sh verify-manifest server-monitor-manager-manifest.json server-monitor-manager-manifest.sig
sudo bash smm-setup.sh verify-release "$ARCHIVE"
sudo bash smm-setup.sh install-control "$ARCHIVE" 127.0.0.1 17443
sudo bash smm-setup.sh mesh-init 127.0.0.1 51820

echo "Checking Control healthz..."
for _ in {1..30}; do
    if sudo curl --fail --silent \
        --cacert /etc/ochenstarik-server-monitor-manager/control-ca.crt \
        "https://127.0.0.1:17443/healthz" >/dev/null; then
        break
    fi
    sleep 1
done
sudo curl --fail --silent --show-error \
    --cacert /etc/ochenstarik-server-monitor-manager/control-ca.crt \
    "https://127.0.0.1:17443/healthz"

echo "Extracting node code and installing agent..."
NODE_CODE=$(sudo bash smm-setup.sh node-code test-node)
export SMM_ENROLL_CODE="$NODE_CODE"
export SMM_ACCEPT_CA_FINGERPRINT=1
sudo --preserve-env=SMM_ENROLL_CODE,SMM_ACCEPT_CA_FINGERPRINT bash smm-setup.sh install-node "$ARCHIVE"

sudo systemctl is-active --quiet ochenstarik-smm-agent.service
sudo systemctl is-active --quiet ochenstarik-smm-control.service

# Verify install-monitor
echo "Installing monitor..."
# Generate a dummy SSH key for the test
ssh-keygen -t ed25519 -N "" -f /tmp/monitor_key
MONITOR_PUB=$(cat /tmp/monitor_key.pub)
sudo bash smm-setup.sh install-monitor "$MONITOR_PUB"

echo "Verifying monitor user and forced command..."
# Run SSH locally as the monitor user (assuming ssh is configured, but actually we can just su into the user or run the forced command directly)
# The forced command is likely defined in ~smm-monitor/.ssh/authorized_keys
MONITOR_CMD=$(sudo cat /var/lib/ochenstarik-server-monitor-manager/monitor/.ssh/authorized_keys | grep -o 'command="[^"]*"' | cut -d'"' -f2)
SNAPSHOT=$(sudo -u ochenstarik-smm-monitor $MONITOR_CMD)

# Simple validation of snapshot fields (since actual values vary, we just check keys)
EXPECTED_KEYS=$(cat tests/contracts/monitor-snapshot-v1.txt | cut -d'=' -f1 | sort)
ACTUAL_KEYS=$(echo "$SNAPSHOT" | cut -d'=' -f1 | sort)

if [[ "$EXPECTED_KEYS" == "$ACTUAL_KEYS" ]]; then
    echo "Monitor snapshot keys match contract."
else
    echo "Monitor snapshot keys mismatch!"
    diff <(echo "$EXPECTED_KEYS") <(echo "$ACTUAL_KEYS") || true
    exit 1
fi

# Verify uninstall
sudo bash smm-setup.sh uninstall-monitor
sudo bash smm-setup.sh uninstall-agent --purge
sudo bash smm-setup.sh uninstall-control --confirm-destroy-control

echo "Positive installation test passed!"
