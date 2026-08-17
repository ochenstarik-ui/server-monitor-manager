#!/bin/bash
# Installs a published release exactly the way an operator does it: public curl
# downloads, checksum verification, signature verification, then the documented
# bootstrap commands. No gh CLI and no token, because the operator has neither —
# and because gh resolves the repository from git context, which the isolated
# workspace deliberately removes.
set -euo pipefail

TAG="${1:-}"
REPOSITORY="${SMM_REPOSITORY:-ochenstarik-ui/server-monitor-manager}"

if [[ -z "$TAG" ]]; then
    echo "Usage: $0 <tag>" >&2
    exit 1
fi

BASE_URL="https://github.com/${REPOSITORY}/releases/download/${TAG}"
CONTROL_PORT=17443
MONITOR_USER="ochenstarik-monitor"
MONITOR_HOME="/var/lib/ochenstarik-monitor"
METRICS_SCRIPT="/usr/local/libexec/ochenstarik-smm-metrics"

echo "Running positive installation test for $TAG..."

if command -v cosign >/dev/null 2>&1; then
    echo "FAIL: cosign is already present before the clean-host installation test" >&2
    exit 1
fi

download() {
    local name="$1"
    curl --fail --silent --show-error --location --retry 3 \
        -o "$name" "${BASE_URL}/${name}" \
        || { echo "FAIL: asset is not downloadable: $name" >&2; exit 1; }
}

case "$(uname -m)" in
    x86_64) RUNTIME="linux-x64" ;;
    aarch64|arm64) RUNTIME="linux-arm64" ;;
    *) echo "FAIL: unsupported architecture $(uname -m)" >&2; exit 1 ;;
esac
ARCHIVE="server-monitor-manager-${RUNTIME}.tar.gz"

download smm-setup.sh
download smm-setup.sh.sha256
sha256sum -c smm-setup.sh.sha256

download "$ARCHIVE"
download "$ARCHIVE.sha256"
sha256sum -c "$ARCHIVE.sha256"

# Signature material must sit beside the archive: verify_archive looks for it there.
download server-monitor-manager-manifest.json
download server-monitor-manager-manifest.sig
download server-monitor-manager-manifest.pem

sudo bash smm-setup.sh --tag "$TAG" install-hub 127.0.0.1 "$CONTROL_PORT" 51820
[[ "$(command -v cosign)" == "/usr/local/bin/cosign" ]] \
    || { echo "FAIL: installer did not provision /usr/local/bin/cosign" >&2; exit 1; }
cosign version >/dev/null

# Exercise the direct bootstrap paths after the short install-hub path has
# provisioned cosign on the otherwise clean runner.
sudo bash smm-setup.sh --tag "$TAG" preflight
sudo bash smm-setup.sh --tag "$TAG" verify-manifest \
    server-monitor-manager-manifest.json \
    server-monitor-manager-manifest.sig \
    server-monitor-manager-manifest.pem
sudo bash smm-setup.sh --tag "$TAG" verify-release "$ARCHIVE"

echo "Checking Control healthz..."
for _ in {1..30}; do
    if sudo curl --fail --silent \
        --cacert /etc/ochenstarik-server-monitor-manager/control-ca.crt \
        "https://127.0.0.1:${CONTROL_PORT}/healthz" >/dev/null; then
        break
    fi
    sleep 1
done
sudo curl --fail --silent --show-error \
    --cacert /etc/ochenstarik-server-monitor-manager/control-ca.crt \
    "https://127.0.0.1:${CONTROL_PORT}/healthz"

echo "Enrolling a node..."
NODE_CODE="$(sudo bash smm-setup.sh --tag "$TAG" node-code test-node)"
# The file did not exist before this test and was created by install-hub above.
# Remove only that test-provisioned copy so install-node is also exercised from
# a host without cosign.
sudo rm -f -- /usr/local/bin/cosign
hash -r
if command -v cosign >/dev/null 2>&1; then
    echo "FAIL: cosign is still present before the clean-host install-node test" >&2
    exit 1
fi
SMM_ENROLL_CODE="$NODE_CODE" SMM_ACCEPT_CA_FINGERPRINT=1 \
    sudo --preserve-env=SMM_ENROLL_CODE,SMM_ACCEPT_CA_FINGERPRINT \
    bash smm-setup.sh --tag "$TAG" install-node
[[ "$(command -v cosign)" == "/usr/local/bin/cosign" ]] \
    || { echo "FAIL: install-node did not provision /usr/local/bin/cosign" >&2; exit 1; }

sudo systemctl is-active --quiet ochenstarik-smm-agent.service
sudo systemctl is-active --quiet ochenstarik-smm-control.service

echo "Installing monitor role..."
ssh-keygen -t ed25519 -N "" -f /tmp/monitor_key -q
sudo bash smm-setup.sh --tag "$TAG" install-monitor "$(cat /tmp/monitor_key.pub)"

echo "Verifying monitor snapshot against the contract..."
sudo grep -Fq "command=\"${METRICS_SCRIPT}\"" "${MONITOR_HOME}/.ssh/authorized_keys" \
    || { echo "FAIL: forced command is not pinned in authorized_keys" >&2; exit 1; }

SNAPSHOT="$(sudo -u "$MONITOR_USER" "$METRICS_SCRIPT")"
EXPECTED_KEYS="$(cut -d'=' -f1 tests/contracts/monitor-snapshot-v1.txt | sort)"
ACTUAL_KEYS="$(cut -d'=' -f1 <<<"$SNAPSHOT" | sort)"

if [[ "$EXPECTED_KEYS" != "$ACTUAL_KEYS" ]]; then
    echo "FAIL: monitor snapshot keys do not match the contract" >&2
    diff <(echo "$EXPECTED_KEYS") <(echo "$ACTUAL_KEYS") >&2 || true
    exit 1
fi
echo "PASS: monitor snapshot matches the contract"

sudo bash smm-setup.sh --tag "$TAG" uninstall-monitor
sudo bash smm-setup.sh --tag "$TAG" uninstall-agent --purge
sudo bash smm-setup.sh --tag "$TAG" uninstall-control --confirm-destroy-control

echo "Positive installation test passed!"
