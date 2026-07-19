#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
bootstrap="$root/deploy/ochenstarik-server-monitor-manager.sh"
helper="$root/deploy/ochenstarik-smm-policy-apply"
emergency="$root/deploy/ochenstarik-smm-emergency"

help_output="$(bash "$bootstrap" --help)"
version_output="$(bash "$bootstrap" --version)"

grep -Fq "install-control ARCHIVE PUBLIC_HOST" <<<"$help_output"
grep -Fq "install-agent ARCHIVE NODE_ID CONTROL_URL CA_CERT" <<<"$help_output"
grep -Fq "install-node ARCHIVE" <<<"$help_output"
grep -Fq "mesh-init PUBLIC_ENDPOINT" <<<"$help_output"
grep -Fq "peer-add SMMPEER1_CODE" <<<"$help_output"
grep -Fq "mesh-status" <<<"$help_output"
grep -Fq "SMM_ENROLL_TOKEN" <<<"$help_output"
grep -Fq "node-code NODE_ID" <<<"$help_output"
grep -Fq "verify-release ARCHIVE" <<<"$help_output"
grep -Fq "node-token NODE_ID" <<<"$help_output"
grep -Eq '^ochenstarik-server-monitor-manager [0-9]+\.[0-9]+\.[0-9]+-' <<<"$version_output"
emergency_help="$(bash "$emergency" --help)"
grep -Fq 'mesh-disable' <<<"$emergency_help"
grep -Fq 'firewall-restore' <<<"$emergency_help"

if bash "$bootstrap" unsupported-action >/dev/null 2>&1; then
    printf '%s\n' "unsupported bootstrap action unexpectedly succeeded" >&2
    exit 1
fi

if env -u SUDO_UID -u SUDO_USER bash "$helper" link-connect source target tcp 22 10 >/dev/null 2>&1; then
    printf '%s\n' "policy helper unexpectedly applied an unconfigured rule" >&2
    exit 1
fi
if bash "$emergency" mesh-disable >/dev/null 2>&1; then
    printf '%s\n' "emergency mutation unexpectedly succeeded without root" >&2
    exit 1
fi

policy_state="$(mktemp -t smm-policy-state.XXXXXXXX)"
printf 'source\t10.77.0.2\tkey-source\tactive\n' >"$policy_state"
printf 'target\t10.77.0.3\tkey-target\tactive\n' >>"$policy_state"
connect_output="$(SMM_POLICY_TESTING=1 SMM_POLICY_STATE_FILE="$policy_state" \
    bash "$helper" link-connect source target tcp 22 10)"
grep -Fq 'ip saddr 10.77.0.2 ip daddr 10.77.0.3 tcp dport 22' <<<"$connect_output"
grep -Fq 'smm:source:target:tcp:22' <<<"$connect_output"
disconnect_output="$(SMM_POLICY_TESTING=1 SMM_POLICY_STATE_FILE="$policy_state" \
    bash "$helper" link-disconnect source target tcp 22)"
grep -Fq 'smm:source:target:tcp:22' <<<"$disconnect_output"
rm -f -- "$policy_state"

fixture="$(mktemp -d -t smm-bootstrap-test.XXXXXXXX)"
trap 'rm -rf -- "$fixture"' EXIT
mkdir -p "$fixture/payload/agent" "$fixture/payload/control" "$fixture/payload/provisioning-helper" "$fixture/payload/deploy" "$fixture/payload/bootstrap"
install -m 0755 /bin/true "$fixture/payload/agent/ochenstarik-smm-agent"
install -m 0755 /bin/true "$fixture/payload/control/ochenstarik-smm-control"
install -m 0755 /bin/true "$fixture/payload/provisioning-helper/ochenstarik-smm-provisioning-helper"
install -m 0755 "$helper" "$fixture/payload/deploy/ochenstarik-smm-policy-apply"
install -m 0755 "$emergency" "$fixture/payload/deploy/ochenstarik-smm-emergency"
install -m 0644 "$root/deploy/ochenstarik-smm-control.service" "$fixture/payload/deploy/"
install -m 0644 "$root/deploy/ochenstarik-smm-agent.service" "$fixture/payload/deploy/"
install -m 0644 "$root/deploy/ochenstarik-smm-provisioning-helper.service" "$fixture/payload/deploy/"
install -m 0644 "$root/deploy/ochenstarik-smm-firewall.service" "$fixture/payload/deploy/"
install -m 0755 "$bootstrap" "$fixture/payload/bootstrap/ochenstarik-server-monitor-manager.sh"
tar -C "$fixture/payload" -czf "$fixture/release.tar.gz" agent control provisioning-helper deploy bootstrap
sha256sum "$fixture/release.tar.gz" >"$fixture/release.tar.gz.sha256"
bash "$bootstrap" verify-release "$fixture/release.tar.gz" >/dev/null

printf '%064d  %s\n' 0 release.tar.gz >"$fixture/release.tar.gz.sha256"
if bash "$bootstrap" verify-release "$fixture/release.tar.gz" >/dev/null 2>&1; then
    printf '%s\n' "corrupt release checksum unexpectedly succeeded" >&2
    exit 1
fi

printf '%s\n' "BOOTSTRAP_CONTRACT=PASS"
