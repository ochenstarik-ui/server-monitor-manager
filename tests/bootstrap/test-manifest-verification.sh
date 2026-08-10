#!/usr/bin/env bash
set -Eeuo pipefail

echo "Running negative tests for manifest verification..."

if ! command -v cosign &> /dev/null; then
    echo "cosign could not be found. Please install it to run these tests."
    exit 1
fi

# Generate test keypair
export COSIGN_PASSWORD=""
cosign generate-key-pair
export SMM_TEST_PUBKEY="cosign.pub"

ARCHIVE_NAME="test-archive.tar.gz"
echo "archive content" > "$ARCHIVE_NAME"
ARCHIVE_HASH=$(sha256sum "$ARCHIVE_NAME" | awk '{print $1}')

cat <<EOF > manifest.json
{
  "hashes": {
    "$ARCHIVE_NAME": "$ARCHIVE_HASH"
  }
}
EOF

cosign sign-blob --yes --key cosign.key --output-signature manifest.sig manifest.json

echo "Test 1: Valid signature and hash"
if ! bash deploy/ochenstarik-server-monitor-manager.sh verify-manifest manifest.json manifest.sig; then
    echo "FAIL: Valid payload rejected"
    exit 1
fi
echo "PASS: Valid payload accepted"

echo "Test 2: Altered byte in archive"
echo "altered content" > "$ARCHIVE_NAME"
# Note: we need to test archive verification for altered archive! The old test used verify-manifest which doesn't check archive.
if bash deploy/ochenstarik-server-monitor-manager.sh verify-release "$ARCHIVE_NAME" >/dev/null 2>&1; then
    echo "FAIL: Altered archive accepted"
    exit 1
fi
echo "PASS: Altered archive rejected"

echo "Test 3: Substituted hash in manifest without resigning"
# Restore archive
echo "archive content" > "$ARCHIVE_NAME"
# Corrupt manifest
cat <<EOF > manifest.json
{
  "hashes": {
    "$ARCHIVE_NAME": "0000000000000000000000000000000000000000000000000000000000000000"
  }
}
EOF
if bash deploy/ochenstarik-server-monitor-manager.sh verify-manifest manifest.json manifest.sig >/dev/null 2>&1; then
    echo "FAIL: Substituted hash accepted"
    exit 1
fi
echo "PASS: Substituted hash rejected"

echo "Test 4: Manifest without signature"
if bash deploy/ochenstarik-server-monitor-manager.sh verify-manifest manifest.json "" >/dev/null 2>&1; then
    echo "FAIL: Missing signature accepted"
    exit 1
fi
echo "PASS: Missing signature rejected"

echo "Test 5: Real alpha.8 manifest fallback matching"
ALPHA8_ARCHIVE="ochenstarik-server-monitor-manager-linux-x64.tar.gz"
wget -qO "$ALPHA8_ARCHIVE" https://github.com/ochenstarik-ui/server-monitor-manager/releases/download/v0.1.0-alpha.8/server-monitor-manager-linux-x64.tar.gz
wget -qO server-monitor-manager-manifest.json https://github.com/ochenstarik-ui/server-monitor-manager/releases/download/v0.1.0-alpha.8/server-monitor-manager-manifest.json
wget -qO server-monitor-manager-manifest.sig https://github.com/ochenstarik-ui/server-monitor-manager/releases/download/v0.1.0-alpha.8/server-monitor-manager-manifest.sig
unset SMM_TEST_PUBKEY
if ! bash deploy/ochenstarik-server-monitor-manager.sh verify-release "$ALPHA8_ARCHIVE" >/dev/null 2>&1; then
    echo "FAIL: Alpha.8 real release verification failed"
    exit 1
fi
echo "PASS: Alpha.8 real release verification succeeded"

echo "All tests passed."

rm -f cosign.key cosign.pub manifest.json manifest.sig "$ARCHIVE_NAME" "$ALPHA8_ARCHIVE" server-monitor-manager-manifest.json server-monitor-manager-manifest.sig
