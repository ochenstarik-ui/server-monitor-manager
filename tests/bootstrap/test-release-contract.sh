#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
setup="$root/deploy/smm-setup.sh"
bootstrap="$root/deploy/ochenstarik-server-monitor-manager.sh"
workflow="$root/.github/workflows/linux-release.yml"
verification_workflow="$root/.github/workflows/release-verification.yml"
windows_workflow="$root/.github/workflows/windows-release.yml"
policy="$root/docs/release-policy.md"
installer_contract="$root/docs/installer-contract.md"
manifest_test="$root/tests/bootstrap/test-manifest-verification.sh"
v1_fixture="$root/tests/fixtures/alpha8-v1-release"

[[ -s "$setup" ]] || {
    printf '%s\n' 'tracked production smm-setup.sh source is missing' >&2
    exit 1
}
bash -n "$setup"
grep -Fq 'readonly DEFAULT_RELEASE_TAG="v0.1.0-alpha.16"' "$setup"
grep -Fq 'install-hub PUBLIC_HOST [HTTPS_PORT] [WG_PORT]' "$setup"
grep -Fxq '  install-node' "$setup"
if grep -Fq 'validate_control_url' "$setup" || grep -Fq '${CONTROL_URL%/}/control' "$setup"; then
    printf '%s\n' 'temporary control URL workaround must not be present in smm-setup.sh' >&2
    exit 1
fi

grep -Fq 'install -m 0755 deploy/smm-setup.sh "$DIST_DIR/smm-setup.sh"' "$workflow"
grep -Fq 'smm-setup.sh.sha256' "$workflow"
grep -Fq 'dist/smm-setup.sh' "$workflow"
grep -Fq "      - 'v*'" "$workflow"
grep -Fq 'contents: write' "$workflow"
grep -Fq 'softprops/action-gh-release@' "$workflow"
grep -Fq 'workflow_dispatch:' "$windows_workflow"
if grep -Eq '^[[:space:]]+tags:' "$windows_workflow"; then
    printf '%s\n' 'Windows packaging workflow must not trigger on tags' >&2
    exit 1
fi
if grep -Fq 'softprops/action-gh-release@' "$windows_workflow"; then
    printf '%s\n' 'Windows packaging workflow must not publish GitHub Release assets' >&2
    exit 1
fi
if grep -Eq 'contents:[[:space:]]*write' "$windows_workflow"; then
    printf '%s\n' 'Windows packaging workflow must not have contents write permission' >&2
    exit 1
fi
grep -Fq 'Published tags and release assets are immutable.' "$policy"
grep -Fq 'publish a new, higher version tag' "$policy"
grep -Fq 'v0.1.0-alpha.8' "$policy"
grep -Fq 'v0.1.0-alpha.10' "$policy"
grep -Fq 'v0.1.0-alpha.11' "$policy"
grep -Fq 'release owner' "$policy"
grep -Fq 'tests/bootstrap/**' "$policy"
grep -Fq 'v0.1.0-alpha.12' "$policy"
grep -Fq "perl -pi -e 's/\\x0D$//' \"\$windows_dir/SHA256SUMS\"" "$workflow"
grep -Fq 'sha256sum -c SHA256SUMS' "$workflow"
grep -Fq -- '--output-certificate server-monitor-manager-manifest.pem' "$workflow"
grep -Fq 'server-monitor-manager-manifest.pem' "$workflow"
grep -Fq 'v0.1.0-alpha.13' "$policy"
grep -Fq 'v0.1.0-alpha.14' "$policy"
grep -Fq 'v0.1.0-alpha.15' "$policy"
grep -Fq 'v0.1.0-alpha.16' "$policy"
grep -Fq 'hash -r' "$root/tests/release-verification/run-positive-installation.sh"
grep -Fq 'readonly COSIGN_VERSION="v3.1.3"' "$bootstrap"
grep -Fq 'readonly COSIGN_SHA256_AMD64="4629c757b7618056f8ddd7e2625ae9fdd94c0372a65049520bc7d9df9efc7f71"' "$bootstrap"
grep -Fq 'readonly COSIGN_SHA256_ARM64="c5d324e091826b0d7a78eb16fef316450b4eb9aaec045611c08ba06f5e73220a"' "$bootstrap"
grep -Fq 'readonly COSIGN_INSTALL_PATH="/usr/local/bin/cosign"' "$bootstrap"
grep -Fq 'ensure_cosign' "$bootstrap"
grep -Fq 'workflow_run:' "$verification_workflow"
grep -Fq 'workflows: ["Release pipeline"]' "$verification_workflow"
grep -Fq "github.event.workflow_run.conclusion == 'success'" "$verification_workflow"
grep -Fq "startsWith(github.event.workflow_run.head_branch, 'v')" "$verification_workflow"
grep -Fq 'workflow_dispatch:' "$verification_workflow"
if grep -Fq 'sigstore/cosign-installer' "$verification_workflow"; then
    printf '%s\n' 'Release Verification must test installer-provisioned cosign' >&2
    exit 1
fi
grep -Fq 'Published release tags and their assets are immutable.' "$installer_contract"
grep -Fq 'publish a new, higher version tag' "$installer_contract"

if grep -Eq 'wget|curl|gh release download|https?://' "$manifest_test"; then
    printf '%s\n' 'bootstrap manifest verification test must not depend on network or published releases' >&2
    exit 1
fi
grep -Fq 'server-monitor-manager-bootstrap-manifest.json' "$manifest_test"
grep -Fq 'SMM_ALLOW_UNSIGNED=1' "$manifest_test"
grep -Fq 'SMM_ALLOW_UNSIGNED=0' "$manifest_test"
grep -Fq -- '--tlog-upload=false' "$manifest_test"
grep -Fq -- '--tlog-upload=false' "$root/tests/bootstrap/test-bootstrap-contract.sh"
grep -Fq 'server-monitor-manager-manifest.pem' "$root/tests/bootstrap/test-bootstrap-contract.sh"
grep -Fq -- '--insecure-ignore-tlog' "$root/deploy/ochenstarik-server-monitor-manager.sh"
grep -Fq 'verify_args=(--certificate "$certificate" --certificate-oidc-issuer "$COSIGN_ISSUER" --certificate-identity-regexp "$COSIGN_IDENTITY_REGEXP")' "$root/deploy/ochenstarik-server-monitor-manager.sh"
grep -Fq 'verify-manifest MANIFEST SIGNATURE CERTIFICATE' "$root/deploy/ochenstarik-server-monitor-manager.sh"
grep -Fq 'verify-manifest requires MANIFEST SIGNATURE CERTIFICATE' "$root/deploy/ochenstarik-server-monitor-manager.sh"
[[ -s "$v1_fixture/server-monitor-manager-bootstrap-manifest.json" ]]
[[ -d "$v1_fixture/archive-root" ]]
grep -Fq '"schema": "smm-bootstrap-manifest/v1"' "$v1_fixture/server-monitor-manager-bootstrap-manifest.json"
grep -Fq '"bootstrap": "ochenstarik-server-monitor-manager.sh"' "$v1_fixture/server-monitor-manager-bootstrap-manifest.json"
grep -Fq '"bootstrap_sha256":' "$v1_fixture/server-monitor-manager-bootstrap-manifest.json"
if grep -Eq 'schemaVersion|artifacts|signature' "$v1_fixture/server-monitor-manager-bootstrap-manifest.json"; then
    printf '%s\n' 'synthetic v1 fixture contains fields that were not in the published alpha.8 schema' >&2
    exit 1
fi

work="$(mktemp -d -t smm-setup-contract.XXXXXXXX)"
trap 'rm -rf -- "$work"' EXIT
mkdir -p "$work/bin" "$work/home"
cat >"$work/inner.sh" <<'INNER'
#!/usr/bin/env bash
set -Eeuo pipefail
printf 'INNER_ARGS='
printf '%s ' "$@"
printf '\n'
case "${1:-}" in
    install-control|install-node)
        archive="$2"
        [[ "$archive" == */server-monitor-manager-linux-*.tar.gz ]] || exit 0
        directory="$(dirname "$archive")"
        for required in \
            server-monitor-manager-manifest.json \
            server-monitor-manager-manifest.sig \
            server-monitor-manager-manifest.pem; do
            [[ -s "$directory/$required" ]] || {
                printf 'missing signed-release asset: %s\n' "$required" >&2
                exit 1
            }
        done
        ;;
esac
INNER
chmod +x "$work/inner.sh"
inner_hash="$(sha256sum "$work/inner.sh" | cut -d' ' -f1)"
printf '%s\n' 'release archive fixture' >"$work/archive.tar.gz"
archive_hash="$(sha256sum "$work/archive.tar.gz" | cut -d' ' -f1)"
printf '%s\n' '{"schema":"smm-manifest/v2"}' >"$work/server-monitor-manager-manifest.json"
printf '%s\n' 'fixture-signature' >"$work/server-monitor-manager-manifest.sig"
printf '%s\n' 'fixture-certificate' >"$work/server-monitor-manager-manifest.pem"
cat >"$work/bin/curl" <<EOF_CURL
#!/usr/bin/env bash
set -Eeuo pipefail
url=""
out=""
while [[ \$# -gt 0 ]]; do
    case "\$1" in
        -o) out="\$2"; shift 2 ;;
        -*) shift ;;
        *) url="\$1"; shift ;;
    esac
done
printf '%s\n' "\$url" >>'$work/urls'
asset="\${url##*/}"
if [[ "\${SMM_TEST_MISSING_ASSET:-}" == "\$asset" ]]; then
    printf 'fixture asset unavailable: %s\n' "\$asset" >&2
    exit 22
fi
case "\$url" in
    */ochenstarik-server-monitor-manager.sh)
        cp '$work/inner.sh' "\$out"
        ;;
    */ochenstarik-server-monitor-manager.sh.sha256)
        printf '%s  %s\n' '$inner_hash' 'ochenstarik-server-monitor-manager.sh' >"\$out"
        ;;
    */server-monitor-manager-linux-x64.tar.gz|*/server-monitor-manager-linux-arm64.tar.gz)
        cp '$work/archive.tar.gz' "\$out"
        ;;
    */server-monitor-manager-linux-x64.tar.gz.sha256|*/server-monitor-manager-linux-arm64.tar.gz.sha256)
        printf '%s  %s\n' '$archive_hash' "\${asset%.sha256}" >"\$out"
        ;;
    */server-monitor-manager-manifest.json|*/server-monitor-manager-manifest.sig|*/server-monitor-manager-manifest.pem)
        cp '$work/'"\$asset" "\$out"
        ;;
    *)
        printf 'unexpected URL: %s\n' "\$url" >&2
        exit 1
        ;;
esac
EOF_CURL
chmod +x "$work/bin/curl"
cat >"$work/bin/uname" <<'EOF_UNAME'
#!/usr/bin/env bash
printf '%s\n' "${SMM_TEST_ARCH:-x86_64}"
EOF_UNAME
chmod +x "$work/bin/uname"

HOME="$work/home" PATH="$work/bin:$PATH" bash "$setup" version >"$work/output"
grep -Fq 'INNER_ARGS=version ' "$work/output"
grep -Fq '/releases/download/v0.1.0-alpha.16/ochenstarik-server-monitor-manager.sh' "$work/urls"
grep -Fq '/releases/download/v0.1.0-alpha.16/ochenstarik-server-monitor-manager.sh.sha256' "$work/urls"

if HOME="$work/home" PATH="$work/bin:$PATH" bash "$setup" install-hub >"$work/invalid.out" 2>&1; then
    printf '%s\n' 'install-hub accepted a missing PUBLIC_HOST' >&2
    exit 1
fi
grep -Fq 'install-hub requires PUBLIC_HOST [HTTPS_PORT] [WG_PORT]' "$work/invalid.out"
if HOME="$work/home" PATH="$work/bin:$PATH" bash "$setup" install-hub host 7443 51820 extra >"$work/invalid.out" 2>&1; then
    printf '%s\n' 'install-hub accepted an extra argument' >&2
    exit 1
fi
if HOME="$work/home" PATH="$work/bin:$PATH" bash "$setup" install-node extra >"$work/invalid.out" 2>&1; then
    printf '%s\n' 'install-node accepted an argument' >&2
    exit 1
fi
grep -Fq 'install-node takes no arguments' "$work/invalid.out"

HOME="$work/home" PATH="$work/bin:$PATH" bash "$setup" install-hub hub.example.com 7443 51820 >"$work/hub.out"
grep -Eq 'INNER_ARGS=install-control .*/server-monitor-manager-linux-x64.tar.gz hub.example.com 7443 ' "$work/hub.out"
grep -Fq 'INNER_ARGS=mesh-init hub.example.com 51820 ' "$work/hub.out"

HOME="$work/home" PATH="$work/bin:$PATH" bash "$setup" install-node >"$work/node.out"
grep -Eq 'INNER_ARGS=install-node .*/server-monitor-manager-linux-x64.tar.gz ' "$work/node.out"

SMM_TEST_ARCH=aarch64 HOME="$work/home" PATH="$work/bin:$PATH" bash "$setup" install-node >"$work/arm-node.out"
grep -Fq '/server-monitor-manager-linux-arm64.tar.gz' "$work/urls"

HOME="$work/home" PATH="$work/bin:$PATH" bash "$setup" -- install-node legacy.tar.gz >"$work/pass-through.out"
grep -Fq 'INNER_ARGS=install-node legacy.tar.gz ' "$work/pass-through.out"

for required in \
    server-monitor-manager-manifest.json \
    server-monitor-manager-manifest.sig \
    server-monitor-manager-manifest.pem; do
    for mode in node hub; do
        if [[ "$mode" == node ]]; then
            install_arguments=(install-node)
        else
            install_arguments=(install-hub hub.example.com)
        fi
        if SMM_TEST_MISSING_ASSET="$required" HOME="$work/home" PATH="$work/bin:$PATH" \
            bash "$setup" "${install_arguments[@]}" >"$work/missing.out" 2>&1; then
            printf '%s continued without %s\n' "$mode" "$required" >&2
            exit 1
        fi
        grep -Fq "required signed-release asset is unavailable: $required" "$work/missing.out"
    done
done

bash "$root/tests/bootstrap/test-cosign-provisioning.sh"

printf '%s\n' 'RELEASE_CONTRACT=PASS'
