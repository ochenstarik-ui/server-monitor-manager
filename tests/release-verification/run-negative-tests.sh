#!/usr/bin/env bash
# tests/release-verification/run-negative-tests.sh
#
# Negative verification tests: confirm that tampered archives, forged hashes,
# missing signatures, and wrong-identity signatures are all rejected.
#
# Uses only curl (no gh CLI, no GH_TOKEN) to stay consistent with the real
# user path tested in run-positive-installation.sh.
#
# Tests 2-4 require verify-manifest which was added in alpha.10.  For older
# releases, these tests are skipped with a note (the feature simply did not
# exist — this is a known release gap, not a verification failure).
set -Eeuo pipefail
IFS=$'\n\t'

TAG="${1:-}"
REPO="ochenstarik-ui/server-monitor-manager"
BASE_URL="https://github.com/${REPO}/releases/download/${TAG}"

if [[ -z "$TAG" ]]; then
    echo "Usage: $0 <tag>"
    exit 1
fi

echo "Running negative tests against release $TAG..."

download() {
    local name="$1"
    curl --fail --silent --show-error --location -o "$name" "${BASE_URL}/${name}"
}

# Download the bootstrap script and release artifacts needed for testing
download ochenstarik-server-monitor-manager.sh
chmod +x ochenstarik-server-monitor-manager.sh

ARCH="$(uname -m | sed -e 's/x86_64/x64/' -e 's/aarch64/arm64/')"
ARCHIVE="server-monitor-manager-linux-${ARCH}.tar.gz"
download "$ARCHIVE"
download "${ARCHIVE}.sha256"
download server-monitor-manager-manifest.json
download server-monitor-manager-manifest.sig

echo "Test 1: Altered byte in archive"
cp "$ARCHIVE" "corrupted-$ARCHIVE"
echo "corrupted" >> "corrupted-$ARCHIVE"
cp "${ARCHIVE}.sha256" "corrupted-${ARCHIVE}.sha256"
if ./ochenstarik-server-monitor-manager.sh verify-release "corrupted-$ARCHIVE" >/dev/null 2>&1; then
    echo "FAIL: Altered archive was accepted!"
    exit 1
fi
echo "PASS: Altered archive rejected."
rm "corrupted-$ARCHIVE" "corrupted-${ARCHIVE}.sha256"

# Tests 2-4 require verify-manifest.  Detect support in the release's bootstrap.
if ./ochenstarik-server-monitor-manager.sh help 2>&1 | grep -q 'verify-manifest'; then
    HAS_VERIFY_MANIFEST=1
    echo "Release bootstrap supports verify-manifest — running signature tests."
else
    HAS_VERIFY_MANIFEST=0
    echo "NOTE: Release $TAG bootstrap does not support verify-manifest."
    echo "      Skipping signature negative tests (tests 2-4)."
    echo "      This is expected for releases before v0.1.0-alpha.10."
fi

if [[ "$HAS_VERIFY_MANIFEST" == "1" ]]; then
    echo "Test 2: Substituted hash in manifest without resigning"
    cp server-monitor-manager-manifest.json corrupted-manifest.json
    sed -i 's/"[a-f0-9]\{64\}"/"0000000000000000000000000000000000000000000000000000000000000000"/g' corrupted-manifest.json
    if ./ochenstarik-server-monitor-manager.sh verify-manifest corrupted-manifest.json server-monitor-manager-manifest.sig >/dev/null 2>&1; then
        echo "FAIL: Manifest with substituted hash accepted!"
        exit 1
    fi
    echo "PASS: Substituted hash rejected."
    rm corrupted-manifest.json

    echo "Test 3: Manifest without signature"
    if ./ochenstarik-server-monitor-manager.sh verify-manifest server-monitor-manager-manifest.json "" >/dev/null 2>&1; then
        echo "FAIL: Manifest without signature accepted!"
        exit 1
    fi
    echo "PASS: Missing signature rejected."

    echo "Test 4: Signature made by another identity"
    export COSIGN_PASSWORD=""
    cosign generate-key-pair
    cosign sign-blob --yes --key cosign.key --output-signature fake.sig server-monitor-manager-manifest.json
    if ./ochenstarik-server-monitor-manager.sh verify-manifest server-monitor-manager-manifest.json fake.sig >/dev/null 2>&1; then
        echo "FAIL: Signature from wrong identity accepted!"
        exit 1
    fi
    echo "PASS: Wrong identity signature rejected."
    rm cosign.key cosign.pub fake.sig
fi

echo "All negative tests passed!"
