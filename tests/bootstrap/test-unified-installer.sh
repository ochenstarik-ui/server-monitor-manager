#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
setup="$root/deploy/smm-setup.sh"
fixture="$(mktemp -d -t smm-unified-installer.XXXXXXXX)"
trap 'rm -rf -- "$fixture"' EXIT
mkdir -p "$fixture/bin" "$fixture/release" "$fixture/cache"

cat >"$fixture/release/ochenstarik-server-monitor-manager.sh" <<'INNER'
#!/usr/bin/env bash
set -Eeuo pipefail
case "$1" in
    install-control) printf 'FAKE_INSTALL_CONTROL=%s,%s\n' "$3" "$4" ;;
    mesh-init) printf 'FAKE_MESH_INIT=%s,%s\n' "$2" "$3" ;;
    control-device-code) [[ "$2" == operator ]]; printf '%s\n' 'SMMDEV1-test-device-code' ;;
    control-ca-fingerprint) printf '%s\n' 'AA:BB:CC:DD' ;;
    install-node)
        [[ -n "${SMM_ENROLL_CODE:-}" && "${SMM_ACCEPT_CA_FINGERPRINT:-}" == 1 ]]
        printf '%s\n' 'SMMPEER1.test-node.test-address.test-key'
        ;;
    *) printf 'PASSTHROUGH=%s\n' "$*" ;;
esac
INNER
chmod 0755 "$fixture/release/ochenstarik-server-monitor-manager.sh"
(
    cd "$fixture/release"
    sha256sum ochenstarik-server-monitor-manager.sh >ochenstarik-server-monitor-manager.sh.sha256
    printf '%s' archive >server-monitor-manager-linux-x64.tar.gz
    sha256sum server-monitor-manager-linux-x64.tar.gz >server-monitor-manager-linux-x64.tar.gz.sha256
    printf '%s' manifest >server-monitor-manager-manifest.json
    printf '%s' signature >server-monitor-manager-manifest.sig
    printf '%s' certificate >server-monitor-manager-manifest.pem
)

cat >"$fixture/bin/curl" <<'CURL'
#!/usr/bin/env bash
set -Eeuo pipefail
destination=""
url=""
while (( $# > 0 )); do
    case "$1" in
        -o) destination="$2"; shift 2 ;;
        http*) url="$1"; shift ;;
        *) shift ;;
    esac
done
if [[ "$url" == https://api.ipify.org ]]; then
    printf '%s' '203.0.113.10'
elif [[ -n "$destination" ]]; then
    cp "$FIXTURE_RELEASE/${url##*/}" "$destination"
else
    exit 2
fi
CURL
chmod 0755 "$fixture/bin/curl"

openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj /CN=fixture-ca \
    -keyout "$fixture/ca.key" -out "$fixture/ca.crt" >/dev/null 2>&1

b64url() { base64 -w 0 | tr '+/' '-_' | tr -d '='; }
control_part="$(printf '%s' 'https://hub.example:7443' | b64url)"
ca_part="$(b64url <"$fixture/ca.crt")"
node_part="$(printf '%s' 'fixture-node' | b64url)"
token_part="$(printf '%s' 'fixture-token' | b64url)"
endpoint_part="$(printf '%s' 'hub.example:51820' | b64url)"
hub_key_part="$(printf '%s' 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=' | b64url)"
address_part="$(printf '%s' '10.77.0.2' | b64url)"
network_part="$(printf '%s' '10.77.0.0/24' | b64url)"
valid_code="SMMNODE2.$control_part.$ca_part.$node_part.$token_part.$endpoint_part.$hub_key_part.$address_part.$network_part"

run_tty() {
    local input="$1" output="$2"
    if ! printf '%b' "$input" | script -qefc \
        "env PATH='$fixture/bin:/usr/bin:/bin' FIXTURE_RELEASE='$fixture/release' SMM_CACHE_DIR='$fixture/cache' bash '$setup'" \
        /dev/null >"$output" 2>&1; then
        return 1
    fi
}

if bash "$setup" </dev/null >"$fixture/no-tty.out" 2>&1; then
    printf '%s\n' 'no-tty interactive invocation unexpectedly succeeded' >&2
    exit 1
fi
grep -Fq 'interactive installation requires a terminal' "$fixture/no-tty.out"
grep -Fq 'install-hub PUBLIC_HOST' "$fixture/no-tty.out"

run_tty $'1\n\n\n\n' "$fixture/hub.out"
grep -Fq 'System:' "$fixture/hub.out"
grep -Fq 'Public address: 203.0.113.10' "$fixture/hub.out"
grep -Fq 'FAKE_INSTALL_CONTROL=203.0.113.10,7443' "$fixture/hub.out"
grep -Fq 'FAKE_MESH_INIT=203.0.113.10,51820' "$fixture/hub.out"
grep -Fq 'SMMDEV1-test-device-code' "$fixture/hub.out"
grep -Fq 'AA:BB:CC:DD' "$fixture/hub.out"

run_tty "2\n$valid_code\ny\n" "$fixture/node.out"
grep -Fq 'Control: https://hub.example:7443' "$fixture/node.out"
grep -Fq 'WireGuard Hub: hub.example:51820' "$fixture/node.out"
grep -Fq 'CA SHA-256:' "$fixture/node.out"
grep -Fq 'SMMPEER1.test-node.test-address.test-key' "$fixture/node.out"
grep -Fq 'operator application' "$fixture/node.out"

tampered_ca_part="$(printf '%s' 'not-a-certificate' | b64url)"
tampered_code="SMMNODE2.$control_part.$tampered_ca_part.$node_part.$token_part.$endpoint_part.$hub_key_part.$address_part.$network_part"
for bad_code in "${valid_code%.*}" "$valid_code.extra" "$tampered_code"; do
    if run_tty "2\n$bad_code\n" "$fixture/bad.out"; then
        printf '%s\n' 'corrupt SMMNODE2 code unexpectedly succeeded' >&2
        exit 1
    fi
    grep -Eq 'invalid|tampered' "$fixture/bad.out"
done

PATH="$fixture/bin:$PATH" FIXTURE_RELEASE="$fixture/release" SMM_CACHE_DIR="$fixture/cache" \
    bash "$setup" version >"$fixture/noninteractive.out"
grep -Fq 'PASSTHROUGH=version' "$fixture/noninteractive.out"

printf '%s\n' 'UNIFIED_INSTALLER=PASS'
