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
if ! bash tests/bootstrap/verify-manifest.sh "$ARCHIVE_NAME" manifest.json manifest.sig; then
    echo "FAIL: Valid payload rejected"
    exit 1
fi
echo "PASS: Valid payload accepted"

echo "Test 2: Altered byte in archive"
echo "altered content" > "$ARCHIVE_NAME"
if bash tests/bootstrap/verify-manifest.sh "$ARCHIVE_NAME" manifest.json manifest.sig >/dev/null 2>&1; then
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
if bash tests/bootstrap/verify-manifest.sh "$ARCHIVE_NAME" manifest.json manifest.sig >/dev/null 2>&1; then
    echo "FAIL: Substituted hash accepted"
    exit 1
fi
echo "PASS: Substituted hash rejected"

echo "Test 4: Manifest without signature"
if bash tests/bootstrap/verify-manifest.sh "$ARCHIVE_NAME" manifest.json "" >/dev/null 2>&1; then
    echo "FAIL: Missing signature accepted"
    exit 1
fi
echo "PASS: Missing signature rejected"

echo "All tests passed."

rm -f cosign.key cosign.pub manifest.json manifest.sig "$ARCHIVE_NAME"
