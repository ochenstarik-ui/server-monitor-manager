#!/usr/bin/env bash
set -Eeuo pipefail

ARCHIVE=$1
MANIFEST=$2
SIGNATURE=${3:-}

if [ -z "$SIGNATURE" ]; then
    echo "Signature is required."
    exit 1
fi

if ! command -v cosign &> /dev/null; then
    echo "cosign could not be found."
    exit 1
fi

if ! command -v jq &> /dev/null; then
    echo "jq could not be found."
    exit 1
fi

# Verify signature
if [ -n "${SMM_TEST_PUBKEY:-}" ]; then
    cosign verify-blob "$MANIFEST" --signature "$SIGNATURE" --key "$SMM_TEST_PUBKEY" >/dev/null 2>&1
else
    cosign verify-blob "$MANIFEST" \
        --signature "$SIGNATURE" \
        --certificate-identity-regexp "^https://github.com/ochenstarik-ui/server-monitor-manager/" \
        --certificate-oidc-issuer "https://token.actions.githubusercontent.com" >/dev/null 2>&1
fi

# Parse expected hash from manifest based on archive name (assuming archive name ends with .tar.gz or .msix)
ARCHIVE_BASENAME=$(basename "$ARCHIVE")
# The manifest schema isn't fully defined yet, but we expect it to contain hashes. 
# We can store them as { "hashes": { "server-monitor-manager-linux-x64.tar.gz": "sha256..." } }
EXPECTED_HASH=$(jq -r ".hashes[\"$ARCHIVE_BASENAME\"]" "$MANIFEST")

if [ "$EXPECTED_HASH" == "null" ] || [ -z "$EXPECTED_HASH" ]; then
    echo "Hash for $ARCHIVE_BASENAME not found in manifest."
    exit 1
fi

ACTUAL_HASH=$(sha256sum "$ARCHIVE" | awk '{print $1}')

if [ "$EXPECTED_HASH" != "$ACTUAL_HASH" ]; then
    echo "Hash mismatch for $ARCHIVE_BASENAME! Expected $EXPECTED_HASH, got $ACTUAL_HASH."
    exit 1
fi

echo "Verification successful."
exit 0
