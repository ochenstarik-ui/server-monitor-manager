#!/bin/bash
set -euo pipefail

TAG="${1:-}"

if [[ -z "$TAG" ]]; then
    echo "Usage: $0 <tag>"
    exit 1
fi

echo "Verifying assets for release $TAG..."

# Fetch the list of assets from the release
ACTUAL_ASSETS=$(gh release view "$TAG" --json assets -q '.assets[].name' | sort)

EXPECTED_ASSETS=$(cat <<EOF | sort
ochenstarik-server-monitor-manager.sh
ochenstarik-server-monitor-manager.sh.sha256
server-monitor-manager-linux-x64.tar.gz
server-monitor-manager-linux-x64.tar.gz.sha256
server-monitor-manager-linux-arm64.tar.gz
server-monitor-manager-linux-arm64.tar.gz.sha256
server-monitor-manager-linux-x64-sbom.json
server-monitor-manager-linux-arm64-sbom.json
server-monitor-manager-win-x64-sbom.json
ServerMonitorManager-win-x64.msix
ServerMonitorManager-test-signing.cer
SHA256SUMS
smm-setup.sh
smm-setup.sh.sha256
server-monitor-manager-manifest.json
server-monitor-manager-manifest.sig
server-monitor-manager-manifest.pem
EOF
)

if [[ "$ACTUAL_ASSETS" == "$EXPECTED_ASSETS" ]]; then
    echo "All expected assets are present."
else
    echo "Asset mismatch!"
    echo "Expected:"
    echo "$EXPECTED_ASSETS"
    echo "---"
    echo "Actual:"
    echo "$ACTUAL_ASSETS"
    echo "---"
    echo "Missing in Actual:"
    comm -23 <(echo "$EXPECTED_ASSETS") <(echo "$ACTUAL_ASSETS")
    echo "Unexpected in Actual:"
    comm -13 <(echo "$EXPECTED_ASSETS") <(echo "$ACTUAL_ASSETS")
    exit 1
fi
