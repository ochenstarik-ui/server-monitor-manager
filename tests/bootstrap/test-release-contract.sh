#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
setup="$root/deploy/smm-setup.sh"
workflow="$root/.github/workflows/linux-release.yml"
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
grep -Fq 'readonly DEFAULT_RELEASE_TAG="v0.1.0-alpha.13"' "$setup"
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
grep -Fq -- '--insecure-ignore-tlog' "$root/deploy/ochenstarik-server-monitor-manager.sh"
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
printf 'INNER_COMMAND=%s\n' "$1"
INNER
chmod +x "$work/inner.sh"
inner_hash="$(sha256sum "$work/inner.sh" | cut -d' ' -f1)"
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
case "\$url" in
    */ochenstarik-server-monitor-manager.sh)
        cp '$work/inner.sh' "\$out"
        ;;
    */ochenstarik-server-monitor-manager.sh.sha256)
        printf '%s  %s\n' '$inner_hash' 'ochenstarik-server-monitor-manager.sh' >"\$out"
        ;;
    *)
        printf 'unexpected URL: %s\n' "\$url" >&2
        exit 1
        ;;
esac
EOF_CURL
chmod +x "$work/bin/curl"

HOME="$work/home" PATH="$work/bin:$PATH" bash "$setup" version >"$work/output"
grep -Fq 'INNER_COMMAND=version' "$work/output"
grep -Fq '/releases/download/v0.1.0-alpha.13/ochenstarik-server-monitor-manager.sh' "$work/urls"
grep -Fq '/releases/download/v0.1.0-alpha.13/ochenstarik-server-monitor-manager.sh.sha256' "$work/urls"

printf '%s\n' 'RELEASE_CONTRACT=PASS'
