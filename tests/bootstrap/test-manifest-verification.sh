#!/usr/bin/env bash
set -Eeuo pipefail

CLEANUP_FILES=()
CLEANUP_DIRS=()
cleanup() {
    rm -f "${CLEANUP_FILES[@]}"
    rm -rf -- "${CLEANUP_DIRS[@]}"
}
trap cleanup EXIT

echo "Running negative tests for manifest verification..."

if ! command -v cosign &> /dev/null; then
    echo "cosign could not be found. Please install it to run these tests."
    exit 1
fi

# Generate test keypair
export COSIGN_PASSWORD=""
cosign generate-key-pair
export SMM_TEST_PUBKEY="cosign.pub"
CLEANUP_FILES+=(cosign.key cosign.pub)

ARCHIVE_NAME="test-archive.tar.gz"
echo "archive content" > "$ARCHIVE_NAME"
ARCHIVE_HASH=$(sha256sum "$ARCHIVE_NAME" | awk '{print $1}')
CLEANUP_FILES+=("$ARCHIVE_NAME")

# Use the canonical manifest name that verify_archive() expects
cat <<EOF > server-monitor-manager-manifest.json
{
  "hashes": {
    "$ARCHIVE_NAME": "$ARCHIVE_HASH"
  }
}
EOF

cosign sign-blob --yes --tlog-upload=false --key cosign.key --output-signature server-monitor-manager-manifest.sig server-monitor-manager-manifest.json
printf '%s\n' 'test-key certificate placeholder' >server-monitor-manager-manifest.pem
CLEANUP_FILES+=(server-monitor-manager-manifest.json server-monitor-manager-manifest.sig server-monitor-manager-manifest.pem)

echo "Test 1: Valid signature and hash"
if ! bash deploy/ochenstarik-server-monitor-manager.sh verify-manifest server-monitor-manager-manifest.json server-monitor-manager-manifest.sig server-monitor-manager-manifest.pem; then
    echo "FAIL: Valid payload rejected"
    exit 1
fi
echo "PASS: Valid payload accepted"

echo "Test 2: Altered byte in archive"
echo "altered content" > "$ARCHIVE_NAME"
# verify-release will call verify_archive → verify_manifest (signature check) then sha256 (hash check).
# The manifest was signed with the original hash, so the archive hash won't match.
if bash deploy/ochenstarik-server-monitor-manager.sh verify-release "$ARCHIVE_NAME" >/dev/null 2>&1; then
    echo "FAIL: Altered archive accepted"
    exit 1
fi
echo "PASS: Altered archive rejected"

echo "Test 3: Substituted hash in manifest without resigning"
# Restore archive
echo "archive content" > "$ARCHIVE_NAME"
# Corrupt manifest (but don't re-sign — signature should now be invalid)
cat <<EOF > server-monitor-manager-manifest.json
{
  "hashes": {
    "$ARCHIVE_NAME": "0000000000000000000000000000000000000000000000000000000000000000"
  }
}
EOF
if bash deploy/ochenstarik-server-monitor-manager.sh verify-manifest server-monitor-manager-manifest.json server-monitor-manager-manifest.sig server-monitor-manager-manifest.pem >/dev/null 2>&1; then
    echo "FAIL: Substituted hash accepted"
    exit 1
fi
echo "PASS: Substituted hash rejected"

echo "Test 4: Manifest without signature"
if bash deploy/ochenstarik-server-monitor-manager.sh verify-manifest server-monitor-manager-manifest.json "" server-monitor-manager-manifest.pem >/dev/null 2>&1; then
    echo "FAIL: Missing signature accepted"
    exit 1
fi
echo "PASS: Missing signature rejected"

echo "Test 5: Manifest without certificate"
unset SMM_TEST_PUBKEY
if output="$(bash deploy/ochenstarik-server-monitor-manager.sh verify-manifest server-monitor-manager-manifest.json server-monitor-manager-manifest.sig "" 2>&1)"; then
    echo "FAIL: Missing certificate accepted"
    exit 1
fi
if [[ "$output" != *"Certificate not found"* ]]; then
    printf 'FAIL: Missing certificate rejection lacked diagnostic. Output: %s\n' "$output" >&2
    exit 1
fi
export SMM_TEST_PUBKEY="cosign.pub"
echo "PASS: Missing certificate rejected with diagnostic"

echo "Test 6: Synthetic pre-alpha.9 v1 release layout"
fixture_root="tests/fixtures/alpha8-v1-release"
fixture_work="$(mktemp -d -t smm-alpha8-v1.XXXXXXXX)"
CLEANUP_DIRS+=("$fixture_work")
cp "$fixture_root/server-monitor-manager-bootstrap-manifest.json" "$fixture_work/"
cp -R "$fixture_root/archive-root" "$fixture_work/archive-root"
# Git for Windows may materialize text fixtures with CRLF. The historical
# manifest hash pins the canonical LF payload used to build the local archive.
perl -pi -e 's/\x0D$//' "$fixture_work/archive-root/bootstrap/ochenstarik-server-monitor-manager.sh"
expected_bootstrap_hash="$(sed -n 's/.*"bootstrap_sha256": "\([0-9a-f]\{64\}\)".*/\1/p' "$fixture_work/server-monitor-manager-bootstrap-manifest.json")"
actual_bootstrap_hash="$(sha256sum "$fixture_work/archive-root/bootstrap/ochenstarik-server-monitor-manager.sh" | awk '{print $1}')"
[[ "$expected_bootstrap_hash" == "$actual_bootstrap_hash" ]] || {
    echo "FAIL: Synthetic v1 manifest bootstrap hash does not match fixture payload"
    exit 1
}
find "$fixture_work/archive-root" -type f -name 'ochenstarik-*' -exec chmod 0755 {} +
ALPHA8_ARCHIVE="$fixture_work/server-monitor-manager-linux-x64.tar.gz"
tar -C "$fixture_work/archive-root" -czf "$ALPHA8_ARCHIVE" \
    agent control provisioning-helper deploy bootstrap
sha256sum "$ALPHA8_ARCHIVE" >"$ALPHA8_ARCHIVE.sha256"

unset SMM_TEST_PUBKEY
if SMM_ALLOW_UNSIGNED=0 bash deploy/ochenstarik-server-monitor-manager.sh verify-release "$ALPHA8_ARCHIVE" >"$fixture_work/strict.out" 2>&1; then
    echo "FAIL: Unsigned v1 fixture accepted without explicit bypass"
    exit 1
fi
if ! grep -Fq 'Manifest, signature, and certificate are required' "$fixture_work/strict.out"; then
    echo "FAIL: Strict v1 rejection lacked expected diagnostic"
    cat "$fixture_work/strict.out" >&2
    exit 1
fi
echo "PASS: Unsigned v1 fixture rejected without bypass"

if ! SMM_ALLOW_UNSIGNED=1 bash deploy/ochenstarik-server-monitor-manager.sh verify-release "$ALPHA8_ARCHIVE" >"$fixture_work/bypass.out" 2>&1; then
    echo "FAIL: Valid v1 fixture rejected with SMM_ALLOW_UNSIGNED=1"
    cat "$fixture_work/bypass.out" >&2
    exit 1
fi
grep -Fq 'falling back to .sha256 file due to SMM_ALLOW_UNSIGNED=1' "$fixture_work/bypass.out" || {
    echo "FAIL: v1 fixture did not exercise checksum fallback"
    exit 1
}
echo "PASS: Valid v1 fixture accepted only with explicit bypass"

echo "All tests passed."
