#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
bootstrap="$root/deploy/ochenstarik-server-monitor-manager.sh"
helper="$root/deploy/ochenstarik-smm-policy-apply"
emergency="$root/deploy/ochenstarik-smm-emergency"
acceptance="$root/tests/acceptance/three-server-mesh.sh"

grep -Fq 'listing="$(/usr/sbin/nft -a list chain' "$helper" || {
    printf '%s\n' "policy status probe must fail closed when nftables cannot be inspected" >&2
    exit 1
}
grep -Fq "grep -Eiq 'No such file or directory|does not exist'" "$helper"
provisioning_helper_unit="$root/deploy/ochenstarik-smm-provisioning-helper.service"

grep -Fq 'EnvironmentFile=/etc/ochenstarik-server-monitor-manager/agent.env' "$provisioning_helper_unit"
grep -Fq 'ReadWritePaths=/var/lib/ochenstarik-server-monitor-manager/provisioning/rollback' "$provisioning_helper_unit"
grep -Fq 'install -d -m 0700 -o root -g root "$STATE_DIR/provisioning/rollback"' "$bootstrap"
if grep -Fq 'SMM_EnrollToken=$ENROLL_TOKEN' "$bootstrap"; then
    printf '%s\n' "enrollment token is exposed through process argv" >&2
    exit 1
fi
grep -Fq 'readonly ENROLLMENT_DIR="${STATE_DIR}-enrollment"' "$bootstrap"
grep -Fq 'install -d -m 0710 -o root -g "$AGENT_USER" "$ENROLLMENT_DIR"' "$bootstrap"
grep -Fq 'token_temp="$(mktemp "$ENROLLMENT_DIR/.enroll-token.XXXXXXXX")"' "$bootstrap"
if grep -Fq '$STATE_DIR/enrollment' "$bootstrap"; then
    printf '%s\n' "enrollment directory is beneath Control-writable state" >&2
    exit 1
fi
grep -Fq 'chmod 0400 "$token_temp"' "$bootstrap"
grep -Fq 'mv -fT -- "$token_temp" "$token_file"' "$bootstrap"
grep -Fq '"SMM_EnrollTokenFile=$token_file"' "$bootstrap"
grep -Fq 'rm -f -- "$token_file"' "$bootstrap"
grep -Fq 'rm -f -- "$ENROLLMENT_TOKEN_FILE"' "$bootstrap"
grep -Fq 'rm -f -- "$ENROLLMENT_TOKEN_TEMP"' "$bootstrap"
grep -Fq 'SMM_AgentUid=$(id -u "$AGENT_USER")' "$bootstrap"
grep -Fq 'refresh_agent_uid() {' "$bootstrap"
grep -Fq 'agent_uid="$(id -u "$AGENT_USER")"' "$bootstrap"
grep -Fq "printf 'SMM_AgentUid=%s\\n' \"\$agent_uid\"" "$bootstrap"
grep -Fq '        refresh_agent_uid' "$bootstrap"
grep -Fq 'temp="$(mktemp "$ETC_DIR/.agent.env.XXXXXXXX")"' "$bootstrap"
grep -Fq 'mv -fT -- "$temp" "$env_file"' "$bootstrap"
refresh_line="$(grep -F -m1 -n '        refresh_agent_uid' "$bootstrap" | cut -d: -f1)"
stop_line="$(grep -F -m1 -n '    systemctl stop "$unit"' "$bootstrap" | cut -d: -f1)"
(( refresh_line < stop_line ))

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
grep -Fq 'readonly RECONCILE_MARKER="$STATE_DIR/mesh/reconcile-requested"' "$emergency"
grep -Fq 'chown root:root "$temporary_marker"' "$emergency"
grep -Fq 'chmod 0600 "$temporary_marker"' "$emergency"
grep -Fq 'mv -f -- "$temporary_marker" "$RECONCILE_MARKER"' "$emergency"
grep -Fq '/usr/bin/flock -x 9' "$emergency"
grep -Fq 'generation="$(</proc/sys/kernel/random/uuid)"' "$emergency"
[[ "$(grep -Fc '    request_reconciliation' "$emergency")" -ge 2 ]]

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
status_output="$(SMM_POLICY_TESTING=1 SMM_POLICY_STATE_FILE="$policy_state" \
    bash "$helper" link-status source target tcp 22)"
[[ "$status_output" == 'disabled' ]] || {
    printf '%s\n' "policy helper returned an invalid factual status" >&2
    exit 1
}
if SMM_POLICY_TESTING=1 SMM_POLICY_STATE_FILE="$policy_state" \
    bash "$helper" link-status source target tcp 22 unexpected >/dev/null 2>&1; then
    printf '%s\n' "policy helper unexpectedly accepted extra link-status arguments" >&2
    exit 1
fi

generation_a='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
generation_b='bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
reconcile_marker="$(mktemp -t smm-reconcile-marker.XXXXXXXX)"
printf '%s\n' "$generation_a" >"$reconcile_marker"
[[ "$(SMM_POLICY_TESTING=1 SMM_POLICY_FLOCK=true SMM_POLICY_RECONCILE_MARKER="$reconcile_marker" \
    bash "$helper" reconcile-status)" == "requested:$generation_a" ]]
printf '%s\n' "$generation_b" >"$reconcile_marker"
SMM_POLICY_TESTING=1 SMM_POLICY_FLOCK=true SMM_POLICY_RECONCILE_MARKER="$reconcile_marker" \
    bash "$helper" reconcile-complete "$generation_a" >/dev/null
[[ "$(<"$reconcile_marker")" == "$generation_b" ]]
SMM_POLICY_TESTING=1 SMM_POLICY_FLOCK=true SMM_POLICY_RECONCILE_MARKER="$reconcile_marker" \
    bash "$helper" reconcile-complete "$generation_b" >/dev/null
[[ ! -e "$reconcile_marker" ]]
[[ "$(SMM_POLICY_TESTING=1 SMM_POLICY_FLOCK=true SMM_POLICY_RECONCILE_MARKER="$reconcile_marker" \
    bash "$helper" reconcile-status)" == 'complete' ]]
if SMM_POLICY_TESTING=1 SMM_POLICY_FLOCK=true SMM_POLICY_RECONCILE_MARKER="$reconcile_marker" \
    bash "$helper" reconcile-complete unexpected extra >/dev/null 2>&1; then
    printf '%s\n' "policy helper unexpectedly accepted extra reconcile-complete arguments" >&2
    exit 1
fi

firewall_error="$(mktemp -t smm-firewall-error.XXXXXXXX)"
if SMM_POLICY_TESTING=1 SMM_POLICY_STATE_FILE="$policy_state" \
    SMM_POLICY_FIREWALL_UNAVAILABLE=1 bash "$helper" \
    link-status source target tcp 22 >/dev/null 2>"$firewall_error"; then
    printf '%s\n' "missing firewall unexpectedly produced a factual Link status" >&2
    exit 1
else
    [[ $? -eq 79 ]]
fi
[[ "$(<"$firewall_error")" == 'mesh.firewall-unavailable' ]]
if SMM_POLICY_TESTING=1 SMM_POLICY_STATE_FILE="$policy_state" \
    SMM_POLICY_FIREWALL_ERROR='permission denied' bash "$helper" \
    link-status source target tcp 22 >/dev/null 2>"$firewall_error"; then
    printf '%s\n' "unknown nft inspection error unexpectedly produced a factual Link status" >&2
    exit 1
else
    [[ $? -eq 78 ]]
fi
grep -Fq 'permission denied' "$firewall_error"
if grep -Fq 'mesh.firewall-unavailable' "$firewall_error"; then
    printf '%s\n' "unknown nft inspection error was misclassified as missing firewall" >&2
    exit 1
fi
rm -f -- "$firewall_error" "$reconcile_marker"
rm -f -- "$policy_state"

extract_shell_function() {
    local name="$1"
    awk -v signature="$name() {" '
        $0 == signature { emitting = 1 }
        emitting { print }
        emitting && $0 == "}" { exit }
    ' "$acceptance"
}
eval "$(extract_shell_function probe_factual_status)"
SOURCE_NODE_ID=source
TARGET_PORT=22
probe_counter="$(mktemp -t smm-factual-probe.XXXXXXXX)"
printf '%s\n' 0 >"$probe_counter"
hub_ssh() {
    local count
    count="$(( $(<"$probe_counter") + 1 ))"
    printf '%s\n' "$count" >"$probe_counter"
    [[ "$count" -eq 1 ]] && printf '%s\n' disabled || printf '%s\n' active
}
if probe_factual_status target active; then
    printf '%s\n' "initial factual mismatch unexpectedly passed" >&2
    exit 1
fi
probe_factual_status target active || {
    printf '%s\n' "factual probe did not allow convergence after an initial mismatch" >&2
    exit 1
}
rm -f -- "$probe_counter" "${reconcile_marker}.lock"

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
