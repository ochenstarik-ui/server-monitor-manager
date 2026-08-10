#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
setup="$root/deploy/smm-setup.sh"
workflow="$root/.github/workflows/linux-release.yml"
policy="$root/docs/release-policy.md"
installer_contract="$root/docs/installer-contract.md"

[[ -s "$setup" ]] || {
    printf '%s\n' 'tracked production smm-setup.sh source is missing' >&2
    exit 1
}
bash -n "$setup"
grep -Fq 'readonly DEFAULT_RELEASE_TAG="v0.1.0-alpha.9"' "$setup"
if grep -Fq 'validate_control_url' "$setup" || grep -Fq '${CONTROL_URL%/}/control' "$setup"; then
    printf '%s\n' 'temporary control URL workaround must not be present in smm-setup.sh' >&2
    exit 1
fi

grep -Fq 'install -m 0755 deploy/smm-setup.sh "$DIST_DIR/smm-setup.sh"' "$workflow"
grep -Fq 'smm-setup.sh.sha256' "$workflow"
grep -Fq 'dist/smm-setup.sh' "$workflow"
grep -Fq 'Published tags and release assets are immutable.' "$policy"
grep -Fq 'publish a new, higher version tag' "$policy"
grep -Fq 'Published release tags and their assets are immutable.' "$installer_contract"
grep -Fq 'publish a new, higher version tag' "$installer_contract"

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
grep -Fq '/releases/download/v0.1.0-alpha.9/ochenstarik-server-monitor-manager.sh' "$work/urls"
grep -Fq '/releases/download/v0.1.0-alpha.9/ochenstarik-server-monitor-manager.sh.sha256' "$work/urls"

printf '%s\n' 'RELEASE_CONTRACT=PASS'
