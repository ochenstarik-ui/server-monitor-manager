#!/bin/bash
set -euo pipefail

TAG="${1:-}"

if [[ -z "$TAG" ]]; then
    echo "Usage: $0 <tag>"
    exit 1
fi

echo "Running negative tests against release $TAG..."

# We will need smm-setup.sh or ochenstarik-server-monitor-manager.sh
# We'll download ochenstarik-server-monitor-manager.sh directly to test verify-release
gh release download "$TAG" -p 'ochenstarik-server-monitor-manager.sh'
chmod +x ochenstarik-server-monitor-manager.sh

ARCHIVE="server-monitor-manager-linux-$(uname -m | sed -e 's/x86_64/x64/' -e 's/aarch64/arm64/').tar.gz"
gh release download "$TAG" -p "$ARCHIVE"
gh release download "$TAG" -p "server-monitor-manager-manifest.json"
gh release download "$TAG" -p "server-monitor-manager-manifest.sig"

echo "Test 1: Altered byte in archive"
cp "$ARCHIVE" "corrupted-$ARCHIVE"
echo "corrupted" >> "corrupted-$ARCHIVE"
if ./ochenstarik-server-monitor-manager.sh verify-release "corrupted-$ARCHIVE" >/dev/null 2>&1; then
    echo "FAIL: Altered archive was accepted!"
    exit 1
fi
echo "PASS: Altered archive rejected."
rm "corrupted-$ARCHIVE"

echo "Test 2: Substituted hash in manifest without resigning"
cp server-monitor-manager-manifest.json corrupted-manifest.json
# Replace all hashes with zeros
sed -i 's/"[a-f0-9]\{64\}"/"0000000000000000000000000000000000000000000000000000000000000000"/g' corrupted-manifest.json
# Test verify-manifest directly
if ./ochenstarik-server-monitor-manager.sh verify-manifest corrupted-manifest.json server-monitor-manager-manifest.sig >/dev/null 2>&1; then
    echo "FAIL: Manifest with substituted hash accepted!"
    exit 1
fi
echo "PASS: Substituted hash rejected."
rm corrupted-manifest.json

echo "Test 3: Manifest without signature"
# We just pass an empty string for the signature file argument
if ./ochenstarik-server-monitor-manager.sh verify-manifest server-monitor-manager-manifest.json "" >/dev/null 2>&1; then
    echo "FAIL: Manifest without signature accepted!"
    exit 1
fi
echo "PASS: Missing signature rejected."

echo "Test 4: Signature made by another identity"
# Generate a local keypair and sign the manifest
export COSIGN_PASSWORD=""
cosign generate-key-pair
cosign sign-blob --yes --key cosign.key --output-signature fake.sig server-monitor-manager-manifest.json
# Verification must fail because ochenstarik-server-monitor-manager.sh enforces keyless OIDC identity!
if ./ochenstarik-server-monitor-manager.sh verify-manifest server-monitor-manager-manifest.json fake.sig >/dev/null 2>&1; then
    echo "FAIL: Signature from wrong identity accepted!"
    exit 1
fi
echo "PASS: Wrong identity signature rejected."
rm cosign.key cosign.pub fake.sig

echo "Test 5: Real alpha.8 manifest fallback matching (verify-release against older release)"
ALPHA8_ARCHIVE="alpha8-archive.tar.gz"
wget -qO "$ALPHA8_ARCHIVE" https://github.com/ochenstarik-ui/server-monitor-manager/releases/download/v0.1.0-alpha.8/server-monitor-manager-linux-x64.tar.gz
wget -qO server-monitor-manager-manifest.json https://github.com/ochenstarik-ui/server-monitor-manager/releases/download/v0.1.0-alpha.8/server-monitor-manager-manifest.json
wget -qO server-monitor-manager-manifest.sig https://github.com/ochenstarik-ui/server-monitor-manager/releases/download/v0.1.0-alpha.8/server-monitor-manager-manifest.sig

if ! ./ochenstarik-server-monitor-manager.sh verify-release "$ALPHA8_ARCHIVE" >/dev/null 2>&1; then
    echo "FAIL: Alpha.8 real release verification failed!"
    exit 1
fi
echo "PASS: Alpha.8 real release verification succeeded."
rm "$ALPHA8_ARCHIVE" server-monitor-manager-manifest.json server-monitor-manager-manifest.sig

echo "All negative tests passed!"
