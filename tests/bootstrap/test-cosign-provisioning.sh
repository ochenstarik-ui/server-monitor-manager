#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
bootstrap="$root/deploy/ochenstarik-server-monitor-manager.sh"

extract_bootstrap_function() {
    local name="$1"
    awk -v signature="$name() {" '
        $0 == signature { emitting = 1 }
        emitting { print }
        emitting && $0 == "}" { exit }
    ' "$bootstrap"
}

ensure_cosign_definition="$(extract_bootstrap_function ensure_cosign)"
[[ -n "$ensure_cosign_definition" ]]
source <(printf '%s\n' "$ensure_cosign_definition")

work="$(mktemp -d -t smm-cosign-contract.XXXXXXXX)"
trap 'rm -rf -- "$work"' EXIT
mkdir -p "$work/bin" "$work/installed"

cat >"$work/cosign-fixture" <<'FIXTURE'
#!/usr/bin/env bash
[[ "${1:-}" == "version" ]]
printf '%s\n' 'cosign fixture'
FIXTURE
chmod +x "$work/cosign-fixture"
fixture_sha256="$(sha256sum "$work/cosign-fixture" | awk '{ print $1 }')"

cat >"$work/bin/curl" <<EOF_CURL
#!/usr/bin/env bash
set -Eeuo pipefail
output=""
url=""
while [[ \$# -gt 0 ]]; do
    case "\$1" in
        --output) output="\$2"; shift 2 ;;
        --*) shift ;;
        *) url="\$1"; shift ;;
    esac
done
printf '%s\n' "\$url" >>'$work/urls'
[[ "\${SMM_TEST_COSIGN_DOWNLOAD_FAIL:-0}" != "1" ]] || exit 22
cp '$work/cosign-fixture' "\$output"
EOF_CURL
chmod +x "$work/bin/curl"

cat >"$work/bin/uname" <<'EOF_UNAME'
#!/usr/bin/env bash
printf '%s\n' "${SMM_TEST_ARCH:-x86_64}"
EOF_UNAME
chmod +x "$work/bin/uname"

run_ensure_cosign() {
    local architecture="$1" amd64_sha="$2" arm64_sha="$3" install_path="$4"
    SMM_TEST_ARCH="$architecture" \
    PATH="$work/installed:$work/bin:/usr/bin:/bin" \
    COSIGN_VERSION="v3.1.3" \
    COSIGN_SHA256_AMD64="$amd64_sha" \
    COSIGN_SHA256_ARM64="$arm64_sha" \
    COSIGN_INSTALL_PATH="$install_path" \
    TEMP_DIR="" \
    ensure_cosign
}

require_root() { :; }
require_command() { command -v "$1" >/dev/null 2>&1 || fail "missing $1"; }
log() { printf '%s\n' "$*"; }
fail() { printf '%s\n' "$*" >&2; exit 1; }

install_path="$work/installed/cosign"
run_ensure_cosign x86_64 "$fixture_sha256" "$fixture_sha256" "$install_path"
[[ -x "$install_path" ]]
[[ "$(stat -c '%a' "$install_path")" == "755" ]]
"$install_path" version >/dev/null
grep -Fq '/v3.1.3/cosign-linux-amd64' "$work/urls"

rm -f -- "$install_path"
if (run_ensure_cosign x86_64 "$(printf '0%.0s' {1..64})" "$fixture_sha256" "$install_path") \
    >"$work/mismatch.out" 2>&1; then
    printf '%s\n' 'tampered cosign checksum was accepted' >&2
    exit 1
fi
grep -Fq 'cosign v3.1.3 checksum mismatch' "$work/mismatch.out"
grep -Fq 'Expected SHA-256: 0000000000000000000000000000000000000000000000000000000000000000' "$work/mismatch.out"
grep -Fq "Install path: $install_path" "$work/mismatch.out"
[[ ! -e "$install_path" ]]

if (SMM_TEST_COSIGN_DOWNLOAD_FAIL=1 \
    run_ensure_cosign aarch64 "$fixture_sha256" "$fixture_sha256" "$install_path") \
    >"$work/download.out" 2>&1; then
    printf '%s\n' 'cosign download failure was accepted' >&2
    exit 1
fi
grep -Fq 'Could not download cosign v3.1.3' "$work/download.out"
grep -Fq "Expected SHA-256: $fixture_sha256" "$work/download.out"
grep -Fq "Install path: $install_path" "$work/download.out"
grep -Fq '/v3.1.3/cosign-linux-arm64' "$work/download.out"

cp "$work/cosign-fixture" "$install_path"
chmod 0755 "$install_path"
: >"$work/urls"
run_ensure_cosign x86_64 "$(printf '0%.0s' {1..64})" "$(printf '0%.0s' {1..64})" "$install_path"
[[ ! -s "$work/urls" ]]

# Bash caches successful command lookups. The release acceptance test removes
# its own provisioned cosign to exercise install-node from a clean state, so it
# must clear that cache before checking PATH again.
(
    PATH="$work/installed:$work/bin:/usr/bin:/bin"
    cosign version >/dev/null
    rm -f -- "$install_path"
    hash -r
    if command -v cosign >/dev/null 2>&1; then
        printf '%s\n' 'cosign remained discoverable after hash reset' >&2
        exit 1
    fi
)

printf '%s\n' 'COSIGN_PROVISIONING=PASS'
