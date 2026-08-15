#!/bin/bash
# Every tampering scenario an operator could hit must be rejected by the release
# artefacts themselves. Downloads use public curl only: gh needs git context that
# the isolated workspace removes, and the operator has neither gh nor a token.
set -euo pipefail

TAG="${1:-}"
REPOSITORY="${SMM_REPOSITORY:-ochenstarik-ui/server-monitor-manager}"

if [[ -z "$TAG" ]]; then
    echo "Usage: $0 <tag>" >&2
    exit 1
fi

BASE_URL="https://github.com/${REPOSITORY}/releases/download/${TAG}"

echo "Running negative tests against release $TAG..."

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

download ochenstarik-server-monitor-manager.sh
chmod +x ochenstarik-server-monitor-manager.sh
download "$ARCHIVE"
download "$ARCHIVE.sha256"
download server-monitor-manager-manifest.json
download server-monitor-manager-manifest.sig
download server-monitor-manager-manifest.pem

echo "Test 1: Altered byte in archive"
cp "$ARCHIVE" "corrupted-$ARCHIVE"
cp "$ARCHIVE.sha256" "corrupted-$ARCHIVE.sha256"
echo "corrupted" >>"corrupted-$ARCHIVE"
if ./ochenstarik-server-monitor-manager.sh verify-release "corrupted-$ARCHIVE" >/dev/null 2>&1; then
    echo "FAIL: Altered archive was accepted!" >&2
    exit 1
fi
echo "PASS: Altered archive rejected."
rm -f "corrupted-$ARCHIVE" "corrupted-$ARCHIVE.sha256"

echo "Test 2: Substituted hash in manifest without resigning"
cp server-monitor-manager-manifest.json corrupted-manifest.json
sed -i 's/"[a-f0-9]\{64\}"/"0000000000000000000000000000000000000000000000000000000000000000"/g' corrupted-manifest.json
if ./ochenstarik-server-monitor-manager.sh verify-manifest corrupted-manifest.json \
    server-monitor-manager-manifest.sig server-monitor-manager-manifest.pem >/dev/null 2>&1; then
    echo "FAIL: Manifest with substituted hash accepted!" >&2
    exit 1
fi
echo "PASS: Substituted hash rejected."
rm -f corrupted-manifest.json

echo "Test 3: Manifest without signature"
if ./ochenstarik-server-monitor-manager.sh verify-manifest server-monitor-manager-manifest.json \
    "" server-monitor-manager-manifest.pem >/dev/null 2>&1; then
    echo "FAIL: Manifest without signature accepted!" >&2
    exit 1
fi
echo "PASS: Missing signature rejected."

echo "Test 4: Signature made by another identity"
export COSIGN_PASSWORD=""
cosign generate-key-pair >/dev/null
cosign sign-blob --yes --key cosign.key \
    --output-signature fake.sig server-monitor-manager-manifest.json >/dev/null
if ./ochenstarik-server-monitor-manager.sh verify-manifest server-monitor-manager-manifest.json \
    fake.sig server-monitor-manager-manifest.pem >/dev/null 2>&1; then
    echo "FAIL: Signature from wrong identity accepted!" >&2
    exit 1
fi
echo "PASS: Wrong identity signature rejected."
rm -f cosign.key cosign.pub fake.sig

echo "Test 5: Missing certificate beside the archive"
mkdir -p no-cert && cp "$ARCHIVE" "$ARCHIVE.sha256" \
    server-monitor-manager-manifest.json server-monitor-manager-manifest.sig no-cert/
if ./ochenstarik-server-monitor-manager.sh verify-release "no-cert/$ARCHIVE" >/dev/null 2>&1; then
    echo "FAIL: Archive accepted without the signing certificate!" >&2
    exit 1
fi
echo "PASS: Missing certificate rejected."
rm -rf no-cert

echo "All negative tests passed!"
