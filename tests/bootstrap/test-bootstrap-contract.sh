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
grep -Fq "control-device-code DEVICE_ID" <<<"$help_output"
grep -Fq "verify-release ARCHIVE" <<<"$help_output"
grep -Fq "node-token NODE_ID" <<<"$help_output"
grep -Eq '^ochenstarik-server-monitor-manager [0-9]+\.[0-9]+\.[0-9]+-' <<<"$version_output"

extract_bootstrap_function() {
    local name="$1"
    awk -v signature="$name() {" '
        $0 == signature { emitting = 1 }
        emitting { print }
        emitting && $0 == "}" { exit }
    ' "$bootstrap"
}
validate_port_definition="$(extract_bootstrap_function validate_port)"
validate_ipv4_literal_definition="$(extract_bootstrap_function validate_ipv4_literal)"
validate_control_url_definition="$(extract_bootstrap_function validate_control_url)"
for accepted_url in \
    https://example.com \
    https://host.example:7443 \
    https://10.0.0.1:7443 \
    'https://[2001:db8::1]:7443' \
    'https://[::1]:7443' \
    'https://[2001:db8::]' \
    'https://[::]' \
    'https://[::ffff:192.0.2.128]' \
    'https://[2001:db8:3:4::192.0.2.33]:7443' \
    'https://[1:2:3:4:5:6:192.0.2.1]' \
    https://example.com/; do
    if ! (fail() { exit 1; }; source <(printf '%s\n%s\n%s\n' "$validate_port_definition" "$validate_ipv4_literal_definition" "$validate_control_url_definition"); validate_control_url "$accepted_url"); then
        printf 'valid Control URL was rejected: %s\n' "$accepted_url" >&2
        exit 1
    fi
done
for rejected_url in \
    http://example.com \
    https:// \
    https://:7443 \
    'https://[2001:db8::1' \
    'https://[::::]' \
    'https://[1:2:3]' \
    'https://[::ffff:192.0.2.999]' \
    'https://[::ffff:192.0.2]' \
    'https://[::ffff:192.0.2.1.5]' \
    'https://[::ffff:192.0.2.x]' \
    'https://[1:2:3:4:5:6:7:192.0.2.1]' \
    'https://[::ffff:192.0.2.1:]' \
    'https://[1:2:3:4:5:6:192.0.2.1:]' \
    'https://[1:2:3:4:5:6:7:8:]' \
    'https://[:1:2:3:4:5:6:7]' \
    'https://[::ffff:18446744073709551617.0.0.1]' \
    'https://[::1]:18446744073709551696' \
    'https://2001:db8::1:7443' \
    'https://example.com:7443:7444' \
    https://example..com \
    https://999.0.0.1 \
    https://18446744073709551617.0.0.1 \
    https://example.com/path \
    https://user@example.com \
    'https://example.com:0' \
    'https://example.com:65536' \
    'https://example.com:18446744073709551696' \
    'https://example.com?query=1' \
    'https://example.com#fragment'; do
    if (fail() { exit 1; }; source <(printf '%s\n%s\n%s\n' "$validate_port_definition" "$validate_ipv4_literal_definition" "$validate_control_url_definition"); validate_control_url "$rejected_url"); then
        printf 'invalid Control URL was accepted: %s\n' "$rejected_url" >&2
        exit 1
    fi
done

for action in status version --version preflight help -h --help; do
    if bash "$bootstrap" "$action" surplus >/dev/null 2>&1; then
        printf 'bootstrap action accepted surplus arguments: %s\n' "$action" >&2
        exit 1
    fi
done
if ! bash "$bootstrap" >/dev/null 2>&1; then
    printf '%s\n' 'bootstrap no-argument help form failed' >&2
    exit 1
fi
for action in verify-release install-control install-agent install-node mesh-init peer-add \
    update-control update-agent rollback node-code control-device-code node-token uninstall-control; do
    if bash "$bootstrap" "$action" >/dev/null 2>&1; then
        printf 'bootstrap action accepted missing arguments: %s\n' "$action" >&2
        exit 1
    fi
done

base64url_encode_definition="$(extract_bootstrap_function base64url_encode)"
create_device_code_definition="$(extract_bootstrap_function create_device_code)"
device_fixture="$(mktemp -d -t smm-device-code.XXXXXXXX)"
printf '%s\n' 'https://control.example:7443' >"$device_fixture/control-public-url"
printf '%s\n' 'fixture-ca' >"$device_fixture/control-ca.crt"
device_code="$(
    ETC_DIR="$device_fixture"
    require_root() { :; }
    require_command() { :; }
    validate_node_id() { [[ "$1" == 'desktop-device' ]]; }
    validate_control_url() { [[ "$1" == 'https://control.example:7443' ]]; }
    run_control_cli() {
        [[ "$1" == 'device-token-create' && "$2" == 'desktop-device' ]]
        printf '%043d\n' 0
    }
    openssl() { printf '%s' 'DER-fixture'; }
    source <(printf '%s\n%s\n' "$base64url_encode_definition" "$create_device_code_definition")
    create_device_code desktop-device
)"
[[ "$device_code" == SMMDEV1-* ]]
device_payload_encoded="${device_code#SMMDEV1-}"
device_payload_encoded="${device_payload_encoded//-/+}"
device_payload_encoded="${device_payload_encoded//_/\/}"
case $(( ${#device_payload_encoded} % 4 )) in
    0) ;;
    2) device_payload_encoded+='==' ;;
    3) device_payload_encoded+='=' ;;
    *) printf '%s\n' 'invalid generated SMMDEV1 base64url length' >&2; exit 1 ;;
esac
device_payload="$(printf '%s' "$device_payload_encoded" | base64 -d)"
grep -Fxq 'VERSION=1' <<<"$device_payload"
grep -Fxq 'DEVICE=desktop-device' <<<"$device_payload"
grep -Fxq 'TOKEN=0000000000000000000000000000000000000000000' <<<"$device_payload"
grep -Fxq 'URL=https://control.example:7443' <<<"$device_payload"
grep -Fxq "CA=$(printf '%s' 'DER-fixture' | base64 -w 0)" <<<"$device_payload"
rm -rf -- "$device_fixture"

grep -Fq 'readonly BOOTSTRAP_COMMAND="/usr/local/sbin/ochenstarik-server-monitor-manager.sh"' "$bootstrap"
grep -Fq 'staging="$(mktemp "$(dirname "$BOOTSTRAP_COMMAND")/.ochenstarik-server-monitor-manager.XXXXXXXX")"' "$bootstrap"
grep -Fq 'mv -fT -- "$staging" "$BOOTSTRAP_COMMAND"' "$bootstrap"
[[ "$(grep -Fc '    install_bootstrap_command' "$bootstrap")" -eq 3 ]]
update_role_definition="$(extract_bootstrap_function update_role)"
validation_line="$(grep -n -m1 'validate_control_state_migration' <<<"$update_role_definition" | cut -d: -f1)"
environment_validation_line="$(grep -n -m1 'validate_control_environment_migration' <<<"$update_role_definition" | cut -d: -f1)"
control_stop_line="$(grep -n -m1 'systemctl stop \"\$unit\"' <<<"$update_role_definition" | cut -d: -f1)"
(( validation_line < control_stop_line && environment_validation_line < control_stop_line ))
guard_arm_line="$(grep -n -m1 'CONTROL_UPDATE_RECOVERY_REQUIRED=1' <<<"$update_role_definition" | cut -d: -f1)"
guard_clear_line="$(grep -n -m1 'CONTROL_UPDATE_RECOVERY_REQUIRED=0' <<<"$update_role_definition" | cut -d: -f1)"
prepare_line="$(grep -n -m1 'prepare_control_state' <<<"$update_role_definition" | cut -d: -f1)"
rewrite_line="$(grep -n -m1 'rewrite_control_environment' <<<"$update_role_definition" | cut -d: -f1)"
bootstrap_line="$(grep -n -m1 'install_bootstrap_command' <<<"$update_role_definition" | cut -d: -f1)"
unit_line="$(grep -n -m1 'install_unit \"\$TEMP_DIR/deploy/\$CONTROL_UNIT\"' <<<"$update_role_definition" | cut -d: -f1)"
enable_line="$(grep -n -m1 'systemctl enable \"\$CONTROL_UNIT\"' <<<"$update_role_definition" | cut -d: -f1)"
binary_line="$(grep -n -m1 'install_tree_atomic \"\$TEMP_DIR/control\"' <<<"$update_role_definition" | cut -d: -f1)"
restart_line="$(grep -n -m1 'systemctl restart \"\$unit\"' <<<"$update_role_definition" | cut -d: -f1)"
active_line="$(grep -n -m1 'systemctl is-active --quiet \"\$unit\"' <<<"$update_role_definition" | cut -d: -f1)"
for guarded_line in "$control_stop_line" "$prepare_line" "$rewrite_line" "$bootstrap_line" \
    "$unit_line" "$enable_line" "$binary_line" "$restart_line" "$active_line"; do
    (( guard_arm_line < guarded_line && guarded_line < guard_clear_line ))
done
grep -Fq 'install_bootstrap_command' <<<"$update_role_definition"
grep -Fq 'install_unit "$TEMP_DIR/deploy/$CONTROL_UNIT" "$CONTROL_UNIT"' <<<"$update_role_definition"
grep -Fq 'install_tree_atomic "$TEMP_DIR/control" "$LIB_DIR/control" "$user"' <<<"$update_role_definition"
grep -Fq 'systemctl restart "$unit"' <<<"$update_role_definition"
grep -Fq 'systemctl is-active --quiet "$unit"' <<<"$update_role_definition"
grep -Fq 'install -d -m 0711 -o root -g root "$ETC_DIR" "$STATE_DIR"' "$bootstrap"
grep -Fq 'install -d -m 0700 -o "$CONTROL_USER" -g "$CONTROL_USER" "$STATE_DIR/control"' "$bootstrap"
grep -Fq 'Control__DatabasePath=$STATE_DIR/control/control.db' "$bootstrap"
grep -Fq 'Control__BackupDirectory=$STATE_DIR/control/backups' "$bootstrap"
validate_control_state_migration_definition="$(extract_bootstrap_function validate_control_state_migration)"
prepare_control_state_definition="$(extract_bootstrap_function prepare_control_state)"
control_state_fixture="$(mktemp -d -t smm-control-state.XXXXXXXX)"
mkdir -p "$control_state_fixture/backups"
printf '%s' database >"$control_state_fixture/control.db"
printf '%s' wal >"$control_state_fixture/control.db-wal"
printf '%s' backup >"$control_state_fixture/backups/manifest.json"
(
    STATE_DIR="$control_state_fixture"
    CONTROL_USER=fixture
    fail() { printf '%s\n' "$*" >&2; exit 1; }
    install() {
        local arguments=()
        while (( $# > 0 )); do
            case "$1" in
                -m|-o|-g) shift 2 ;;
                *) arguments+=("$1"); shift ;;
            esac
        done
        command install "${arguments[@]}"
    }
    chown() { :; }
    source <(printf '%s\n%s\n' "$validate_control_state_migration_definition" "$prepare_control_state_definition")
    validate_control_state_migration
    prepare_control_state
)
[[ ! -e "$control_state_fixture/control.db" ]]
[[ "$(<"$control_state_fixture/control/control.db")" == database ]]
[[ "$(<"$control_state_fixture/control/control.db-wal")" == wal ]]
[[ "$(<"$control_state_fixture/control/backups/manifest.json")" == backup ]]
if [[ "$(uname -s)" != MINGW* ]]; then
    [[ "$(stat -c '%a' "$control_state_fixture/control")" == 700 ]]
    [[ "$(stat -c '%a' "$control_state_fixture/control/control.db")" == 600 ]]
fi
printf '%s' conflict >"$control_state_fixture/control.db"
if (
    STATE_DIR="$control_state_fixture"
    fail() { exit 1; }
    source <(printf '%s\n' "$validate_control_state_migration_definition")
    validate_control_state_migration
); then
    printf '%s\n' 'conflicting legacy and role-isolated Control state was accepted' >&2
    exit 1
fi
rm -rf -- "$control_state_fixture"

validate_control_environment_migration_definition="$(extract_bootstrap_function validate_control_environment_migration)"
rewrite_control_environment_definition="$(extract_bootstrap_function rewrite_control_environment)"
alpha7_fixture="$(mktemp -d -t smm-alpha7-update.XXXXXXXX)"
mkdir -p "$alpha7_fixture/state/backups" "$alpha7_fixture/etc"
printf '%s' alpha7-db >"$alpha7_fixture/state/control.db"
printf '%s' alpha7-wal >"$alpha7_fixture/state/control.db-wal"
printf '%s' alpha7-shm >"$alpha7_fixture/state/control.db-shm"
printf '%s' alpha7-backup >"$alpha7_fixture/state/backups/manifest.json"
cat >"$alpha7_fixture/etc/control.env" <<EOF
# alpha.7 fixture: preserve comments and every unrelated value
ASPNETCORE_URLS=https://0.0.0.0:7443
Control__DatabasePath=$alpha7_fixture/state/control.db
Control__CertificateAuthorityPath=/custom/control-ca.pfx
Control__BackupDirectory=$alpha7_fixture/state/backups
Control__LinkReconciliationSeconds=777
CUSTOM_VALUE=spaces are preserved exactly
EOF
cp "$alpha7_fixture/etc/control.env" "$alpha7_fixture/original.env"
(
    STATE_DIR="$alpha7_fixture/state"
    ETC_DIR="$alpha7_fixture/etc"
    CONTROL_USER=fixture
    fail() { printf '%s\n' "$*" >&2; exit 1; }
    install() {
        local arguments=()
        while (( $# > 0 )); do
            case "$1" in -m|-o|-g) shift 2 ;; *) arguments+=("$1"); shift ;; esac
        done
        command install "${arguments[@]}"
    }
    chown() { :; }
    source <(printf '%s\n%s\n%s\n%s\n' \
        "$validate_control_state_migration_definition" \
        "$prepare_control_state_definition" \
        "$validate_control_environment_migration_definition" \
        "$rewrite_control_environment_definition")
    validate_control_state_migration
    validate_control_environment_migration
    prepare_control_state
    rewrite_control_environment
)
[[ "$(<"$alpha7_fixture/state/control/control.db")" == alpha7-db ]]
[[ "$(<"$alpha7_fixture/state/control/control.db-wal")" == alpha7-wal ]]
[[ "$(<"$alpha7_fixture/state/control/control.db-shm")" == alpha7-shm ]]
[[ "$(<"$alpha7_fixture/state/control/backups/manifest.json")" == alpha7-backup ]]
expected_env="$(sed \
    -e "s|^Control__DatabasePath=.*|Control__DatabasePath=$alpha7_fixture/state/control/control.db|" \
    -e "s|^Control__BackupDirectory=.*|Control__BackupDirectory=$alpha7_fixture/state/control/backups|" \
    "$alpha7_fixture/original.env")"
[[ "$(<"$alpha7_fixture/etc/control.env")" == "$expected_env" ]]
printf '%s\n' "Control__DatabasePath=$alpha7_fixture/state/control/control.db" \
    >>"$alpha7_fixture/etc/control.env"
if (
    ETC_DIR="$alpha7_fixture/etc"
    fail() { exit 1; }
    source <(printf '%s\n' "$validate_control_environment_migration_definition")
    validate_control_environment_migration
); then
    printf '%s\n' 'conflicting Control environment paths were accepted' >&2
    exit 1
fi
rm -rf -- "$alpha7_fixture"

record_control_legacy_state_definition="$(extract_bootstrap_function record_control_legacy_state)"
reverse_control_state_migration_definition="$(extract_bootstrap_function reverse_control_state_migration)"
restore_control_update_backup_definition="$(extract_bootstrap_function restore_control_update_backup)"
restore_control_binary_from_archive_definition="$(extract_bootstrap_function restore_control_binary_from_archive)"
recover_control_update_definition="$(extract_bootstrap_function recover_control_update)"
[[ -n "$record_control_legacy_state_definition" ]]
[[ -n "$reverse_control_state_migration_definition" ]]
[[ -n "$restore_control_update_backup_definition" ]]
[[ -n "$restore_control_binary_from_archive_definition" ]]
[[ -n "$recover_control_update_definition" ]]

recovery_fixture="$(mktemp -d -t smm-control-recovery.XXXXXXXX)"
recovery_root="$recovery_fixture/root"
archive_root="$recovery_fixture/archive-root"
mkdir -p \
    "$recovery_root/var/lib/ochenstarik-server-monitor-manager/backups" \
    "$recovery_root/etc/ochenstarik-server-monitor-manager" \
    "$recovery_root/etc/systemd/system" \
    "$recovery_root/usr/local/lib/ochenstarik-server-monitor-manager/control" \
    "$archive_root/etc/ochenstarik-server-monitor-manager" \
    "$archive_root/etc/systemd/system" \
    "$archive_root/usr/local/lib/ochenstarik-server-monitor-manager/control"
printf '%s' original-db >"$recovery_root/var/lib/ochenstarik-server-monitor-manager/control.db"
printf '%s' original-wal >"$recovery_root/var/lib/ochenstarik-server-monitor-manager/control.db-wal"
printf '%s' original-shm >"$recovery_root/var/lib/ochenstarik-server-monitor-manager/control.db-shm"
printf '%s' original-backup >"$recovery_root/var/lib/ochenstarik-server-monitor-manager/backups/identity"
printf '%s' old-binary >"$archive_root/usr/local/lib/ochenstarik-server-monitor-manager/control/ochenstarik-smm-control"
printf '%s\n' 'Control__DatabasePath=/var/lib/ochenstarik-server-monitor-manager/control.db' \
    >"$archive_root/etc/ochenstarik-server-monitor-manager/control.env"
printf '%s' old-unit >"$archive_root/etc/systemd/system/ochenstarik-smm-control.service"
mkdir -p "$recovery_fixture/bootstrap-backups"
tar -C "$archive_root" -czf "$recovery_fixture/bootstrap-backups/alpha7.tar.gz" \
    usr/local/lib/ochenstarik-server-monitor-manager/control \
    etc/ochenstarik-server-monitor-manager/control.env \
    etc/systemd/system/ochenstarik-smm-control.service

(
    STATE_DIR="$recovery_root/var/lib/ochenstarik-server-monitor-manager"
    CONTROL_USER=fixture
    CONTROL_UPDATE_LEGACY_ITEMS=()
    fail() { printf '%s\n' "$*" >&2; exit 1; }
    install() {
        local arguments=()
        while (( $# > 0 )); do
            case "$1" in -m|-o|-g) shift 2 ;; *) arguments+=("$1"); shift ;; esac
        done
        command install "${arguments[@]}"
    }
    chown() { :; }
    source <(printf '%s\n%s\n%s\n' \
        "$record_control_legacy_state_definition" \
        "$prepare_control_state_definition" \
        "$reverse_control_state_migration_definition")
    record_control_legacy_state
    prepare_control_state
    [[ "$(<"$STATE_DIR/control/control.db")" == original-db ]]
    reverse_control_state_migration
    [[ "$(<"$STATE_DIR/control.db")" == original-db ]]
    [[ "$(<"$STATE_DIR/control.db-wal")" == original-wal ]]
    [[ "$(<"$STATE_DIR/control.db-shm")" == original-shm ]]
    [[ "$(<"$STATE_DIR/backups/identity")" == original-backup ]]
    [[ ! -e "$STATE_DIR/control/control.db" ]]
)

printf '%s' new-binary >"$recovery_root/usr/local/lib/ochenstarik-server-monitor-manager/control/ochenstarik-smm-control"
printf '%s\n' 'Control__DatabasePath=/var/lib/ochenstarik-server-monitor-manager/control/control.db' \
    >"$recovery_root/etc/ochenstarik-server-monitor-manager/control.env"
printf '%s' new-unit >"$recovery_root/etc/systemd/system/ochenstarik-smm-control.service"
(
    STATE_DIR="$recovery_root/var/lib/ochenstarik-server-monitor-manager"
    BACKUP_DIR="$recovery_fixture/bootstrap-backups"
    CONTROL_UNIT=ochenstarik-smm-control.service
    CONTROL_UPDATE_LEGACY_ITEMS=()
    systemctl() { :; }
    fail() { printf '%s\n' "$*" >&2; exit 1; }
    source <(printf '%s\n%s\n%s\n%s\n' \
        "$reverse_control_state_migration_definition" \
        "$restore_control_update_backup_definition" \
        "$restore_control_binary_from_archive_definition" \
        "$recover_control_update_definition")
    restore_control_binary_from_archive "$BACKUP_DIR/alpha7.tar.gz" "$recovery_root"
    [[ "$(<"$recovery_root/usr/local/lib/ochenstarik-server-monitor-manager/control/ochenstarik-smm-control")" == old-binary ]]
    [[ "$(<"$recovery_root/etc/ochenstarik-server-monitor-manager/control.env")" == \
        'Control__DatabasePath=/var/lib/ochenstarik-server-monitor-manager/control/control.db' ]]
)

rm -rf -- "$recovery_root/var/lib/ochenstarik-server-monitor-manager/control"
mkdir -p "$recovery_root/var/lib/ochenstarik-server-monitor-manager/control/backups"
mv "$recovery_root/var/lib/ochenstarik-server-monitor-manager/control.db" \
    "$recovery_root/var/lib/ochenstarik-server-monitor-manager/control/control.db"
mv "$recovery_root/var/lib/ochenstarik-server-monitor-manager/control.db-wal" \
    "$recovery_root/var/lib/ochenstarik-server-monitor-manager/control/control.db-wal"
mv "$recovery_root/var/lib/ochenstarik-server-monitor-manager/control.db-shm" \
    "$recovery_root/var/lib/ochenstarik-server-monitor-manager/control/control.db-shm"
mv "$recovery_root/var/lib/ochenstarik-server-monitor-manager/backups/identity" \
    "$recovery_root/var/lib/ochenstarik-server-monitor-manager/control/backups/identity"
rmdir "$recovery_root/var/lib/ochenstarik-server-monitor-manager/backups"
printf '%s' new-binary >"$recovery_root/usr/local/lib/ochenstarik-server-monitor-manager/control/ochenstarik-smm-control"
printf '%s\n' 'Control__DatabasePath=/var/lib/ochenstarik-server-monitor-manager/control/control.db' \
    >"$recovery_root/etc/ochenstarik-server-monitor-manager/control.env"
printf '%s' new-unit >"$recovery_root/etc/systemd/system/ochenstarik-smm-control.service"
(
    STATE_DIR="$recovery_root/var/lib/ochenstarik-server-monitor-manager"
    BACKUP_DIR="$recovery_fixture/bootstrap-backups"
    CONTROL_UNIT=ochenstarik-smm-control.service
    CONTROL_UPDATE_LEGACY_ITEMS=(control.db control.db-wal control.db-shm backups)
    systemctl() { :; }
    fail() { printf '%s\n' "$*" >&2; exit 1; }
    source <(printf '%s\n%s\n%s\n' \
        "$reverse_control_state_migration_definition" \
        "$restore_control_update_backup_definition" \
        "$recover_control_update_definition")
    recover_control_update alpha7 "$recovery_root"
)
[[ "$(<"$recovery_root/var/lib/ochenstarik-server-monitor-manager/control.db")" == original-db ]]
[[ "$(<"$recovery_root/var/lib/ochenstarik-server-monitor-manager/control.db-wal")" == original-wal ]]
[[ "$(<"$recovery_root/var/lib/ochenstarik-server-monitor-manager/control.db-shm")" == original-shm ]]
[[ "$(<"$recovery_root/var/lib/ochenstarik-server-monitor-manager/backups/identity")" == original-backup ]]
[[ "$(<"$recovery_root/usr/local/lib/ochenstarik-server-monitor-manager/control/ochenstarik-smm-control")" == old-binary ]]
[[ "$(<"$recovery_root/etc/ochenstarik-server-monitor-manager/control.env")" == \
    'Control__DatabasePath=/var/lib/ochenstarik-server-monitor-manager/control.db' ]]
[[ "$(<"$recovery_root/etc/systemd/system/ochenstarik-smm-control.service")" == old-unit ]]
rm -rf -- "$recovery_fixture"

cleanup_definition="$(extract_bootstrap_function cleanup)"
[[ -n "$cleanup_definition" ]]
for failure_step in stop state environment bootstrap unit enable binary restart active; do
    guard_fixture="$(mktemp -d -t smm-control-guard.XXXXXXXX)"
    marker="$guard_fixture.recovered"
    set +e
    (
        set -Eeuo pipefail
        PROGRAM=test-bootstrap
        TEMP_DIR="$guard_fixture"
        ENROLLMENT_TOKEN_FILE=""
        ENROLLMENT_TOKEN_TEMP=""
        CONTROL_UPDATE_BACKUP_ID=fixture-backup
        CONTROL_UPDATE_RECOVERY_REQUIRED=1
        marker="$marker"
        failure_step="$failure_step"
        log() { :; }
        recover_control_update() { printf '%s' "$failure_step" >"$marker"; }
        source <(printf '%s\n' "$cleanup_definition")
        trap cleanup EXIT
        false
    ) >/dev/null 2>&1
    guard_status=$?
    set -e
    (( guard_status != 0 )) || {
        printf 'injected Control update failure was ignored: %s\n' "$failure_step" >&2
        exit 1
    }
    [[ -f "$marker" && "$(<"$marker")" == "$failure_step" ]] || {
        printf 'Control recovery guard was not invoked for: %s\n' "$failure_step" >&2
        exit 1
    }
    rm -f -- "$marker"
done

role_is_installed_definition="$(extract_bootstrap_function role_is_installed)"
remove_shared_ca_if_unused_definition="$(extract_bootstrap_function remove_shared_ca_if_unused)"
uninstall_agent_definition="$(extract_bootstrap_function uninstall_agent)"
uninstall_control_definition="$(extract_bootstrap_function uninstall_control)"
grep -Fq 'remove_shared_ca_if_unused' <<<"$uninstall_agent_definition"
grep -Fq 'remove_shared_ca_if_unused' <<<"$uninstall_control_definition"
if grep -Fq 'control-ca.crt' <<<"$uninstall_agent_definition$uninstall_control_definition"; then
    printf '%s\n' 'a role uninstaller deletes the shared CA directly' >&2
    exit 1
fi
role_fixture="$(mktemp -d -t smm-role-uninstall.XXXXXXXX)"
mkdir -p "$role_fixture/etc" "$role_fixture/lib/control" "$role_fixture/lib/agent"
printf '%s' ca >"$role_fixture/etc/control-ca.crt"
(
    ETC_DIR="$role_fixture/etc"; LIB_DIR="$role_fixture/lib"
    source <(printf '%s\n%s\n' "$role_is_installed_definition" "$remove_shared_ca_if_unused_definition")
    rm -rf "$LIB_DIR/agent"
    remove_shared_ca_if_unused
)
[[ -f "$role_fixture/etc/control-ca.crt" ]]
rm -rf "$role_fixture/lib/control"
(
    ETC_DIR="$role_fixture/etc"; LIB_DIR="$role_fixture/lib"
    source <(printf '%s\n%s\n' "$role_is_installed_definition" "$remove_shared_ca_if_unused_definition")
    remove_shared_ca_if_unused
)
[[ ! -e "$role_fixture/etc/control-ca.crt" ]]
mkdir -p "$role_fixture/lib/control" "$role_fixture/lib/agent"
printf '%s' ca >"$role_fixture/etc/control-ca.crt"
(
    ETC_DIR="$role_fixture/etc"; LIB_DIR="$role_fixture/lib"
    source <(printf '%s\n%s\n' "$role_is_installed_definition" "$remove_shared_ca_if_unused_definition")
    rm -rf "$LIB_DIR/control"
    remove_shared_ca_if_unused
)
[[ -f "$role_fixture/etc/control-ca.crt" ]]
rm -rf "$role_fixture/lib/agent"
(
    ETC_DIR="$role_fixture/etc"; LIB_DIR="$role_fixture/lib"
    source <(printf '%s\n%s\n' "$role_is_installed_definition" "$remove_shared_ca_if_unused_definition")
    remove_shared_ca_if_unused
)
[[ ! -e "$role_fixture/etc/control-ca.crt" ]]
rm -rf -- "$role_fixture"

grep -Fq 'UMask=0077' "$root/deploy/ochenstarik-smm-control.service"
grep -Fq 'ReadWritePaths=/var/lib/ochenstarik-server-monitor-manager/control' "$root/deploy/ochenstarik-smm-control.service"
native_smoke="$root/tests/bootstrap/run-native-systemd-smoke.sh"
grep -Fq 'node_code="$(sudo "$system_bootstrap" node-code smoke-node)"' "$native_smoke"
grep -Fq 'export SMM_ENROLL_CODE="$node_code"' "$native_smoke"
grep -Fq 'export SMM_ACCEPT_CA_FINGERPRINT=1' "$native_smoke"
grep -Fq 'sudo test -s /var/lib/ochenstarik-server-monitor-manager/agent/agent.pfx' "$native_smoke"
grep -Fq 'sudo systemctl is-active --quiet ochenstarik-smm-agent.service' "$native_smoke"
grep -Fq 'device_code="$(sudo "$system_bootstrap" control-device-code smoke-device)"' "$native_smoke"
grep -Fq '[[ "$device_code" == SMMDEV1-* ]]' "$native_smoke"
grep -Fq 'sudo --preserve-env=SMM_ENROLL_CODE,SMM_ACCEPT_CA_FINGERPRINT' "$native_smoke"
if grep -Fq 'sudo env SMM_ENROLL_CODE=' "$native_smoke"; then
    printf '%s\n' 'native smoke exposes the enrollment code through env argv' >&2
    exit 1
fi
grep -Fq 'rm -rf -- "$LIB_DIR/control" "$STATE_DIR/control"' "$bootstrap"
if grep -Fq 'install -d -m 0750 -o root -g "$AGENT_USER" "$ETC_DIR"' "$bootstrap"; then
    printf '%s\n' 'Agent installation still takes ownership of the shared configuration parent' >&2
    exit 1
fi
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

policy_listing="$(mktemp -t smm-policy-listing.XXXXXXXX)"
cat >"$policy_listing" <<'EOF'
ip saddr 10.77.0.2 ip daddr 10.77.0.3 tcp dport 22 counter accept comment "smm:source:target:tcp:22" # handle 5
counter accept comment "foreign:keep-me" # handle 6
counter accept comment "smm:source:target:tcp:22" # handle 7
counter accept comment "smm:FORGED:target:tcp:22" # handle 8
EOF
list_error="$(mktemp -t smm-policy-list-error.XXXXXXXX)"
list_output="$(SMM_POLICY_TESTING=1 SMM_POLICY_LISTING_FILE="$policy_listing" \
    bash "$helper" link-list 2>"$list_error")"
[[ "$list_output" == $'source\ttarget\ttcp\t22\nsource\ttarget\ttcp\t22' ]]
if grep -Fq 'foreign:keep-me' <<<"$list_output"; then
    printf '%s\n' "policy helper exposed a foreign nftables comment" >&2
    exit 1
fi
grep -Fq 'forged managed comment ignored' "$list_error"
empty_listing="$(mktemp -t smm-policy-empty-listing.XXXXXXXX)"
empty_list_output="$(SMM_POLICY_TESTING=1 SMM_POLICY_LISTING_FILE="$empty_listing" \
    bash "$helper" link-list)"
[[ -z "$empty_list_output" ]]
if SMM_POLICY_TESTING=1 SMM_POLICY_FIREWALL_UNAVAILABLE=1 \
    bash "$helper" link-list >/dev/null 2>"$list_error"; then
    printf '%s\n' "missing Link policy table unexpectedly produced a listing" >&2
    exit 1
else
    [[ $? -eq 79 ]]
fi
[[ "$(<"$list_error")" == 'mesh.firewall-unavailable' ]]
if SMM_POLICY_TESTING=1 bash "$helper" link-list unexpected >/dev/null 2>&1; then
    printf '%s\n' "policy helper unexpectedly accepted extra link-list arguments" >&2
    exit 1
fi
inactive_state="$(mktemp -t smm-policy-inactive-state.XXXXXXXX)"
printf 'source\t10.77.0.2\tkey-source\tactive\n' >"$inactive_state"
printf 'target\t10.77.0.3\t-\treserved\n' >>"$inactive_state"
if SMM_POLICY_TESTING=1 SMM_POLICY_STATE_FILE="$inactive_state" \
    bash "$helper" link-connect source target tcp 22 10 >/dev/null 2>"$list_error"; then
    printf '%s\n' "policy helper unexpectedly activated a Link to a reserved Node" >&2
    exit 1
else
    [[ $? -eq 80 ]]
fi
[[ "$(<"$list_error")" == 'mesh.node-not-activated' ]]
printf 'source\t10.77.0.2\tkey-source\tactive\n' >"$inactive_state"
if SMM_POLICY_TESTING=1 SMM_POLICY_STATE_FILE="$inactive_state" \
    bash "$helper" link-connect source target tcp 22 10 >/dev/null 2>"$list_error"; then
    printf '%s\n' "policy helper unexpectedly activated a Link to a missing Node" >&2
    exit 1
else
    [[ $? -eq 80 ]]
fi
[[ "$(<"$list_error")" == 'mesh.node-not-activated' ]]
printf 'source\t10.77.0.2\tkey-source\tactive\n' >"$inactive_state"
printf 'target\t\tkey-target\tactive\n' >>"$inactive_state"
if SMM_POLICY_TESTING=1 SMM_POLICY_STATE_FILE="$inactive_state" \
    bash "$helper" link-connect source target tcp 22 10 >/dev/null 2>"$list_error"; then
    printf '%s\n' "policy helper unexpectedly accepted a blank Node address" >&2
    exit 1
else
    [[ $? -eq 78 ]]
fi
[[ "$(<"$list_error")" == 'policy helper: node has no valid mesh address: target' ]]
printf 'source\t10.77.0.2\tkey-source\tactive\n' >"$inactive_state"
printf 'target\tnot-an-ip\tkey-target\tactive\n' >>"$inactive_state"
if SMM_POLICY_TESTING=1 SMM_POLICY_STATE_FILE="$inactive_state" \
    bash "$helper" link-connect source target tcp 22 10 >/dev/null 2>"$list_error"; then
    printf '%s\n' "policy helper unexpectedly accepted an invalid Node address" >&2
    exit 1
else
    [[ $? -eq 78 ]]
fi
[[ "$(<"$list_error")" == 'policy helper: node has no valid mesh address: target' ]]
printf 'source\t10.77.0.2\tkey-source\tactive\n' >"$inactive_state"
printf 'target\t999.77.0.3\tkey-target\tactive\n' >>"$inactive_state"
if SMM_POLICY_TESTING=1 SMM_POLICY_STATE_FILE="$inactive_state" \
    bash "$helper" link-connect source target tcp 22 10 >/dev/null 2>"$list_error"; then
    printf '%s\n' "policy helper unexpectedly accepted an out-of-range Node address" >&2
    exit 1
else
    [[ $? -eq 78 ]]
fi
[[ "$(<"$list_error")" == 'policy helper: node has no valid mesh address: target' ]]
printf 'source\t10.77.0.2\tkey-source\tactive\n' >"$inactive_state"
printf 'target\t10.77.0.3\tkey-target\tgarbage!\n' >>"$inactive_state"
if SMM_POLICY_TESTING=1 SMM_POLICY_STATE_FILE="$inactive_state" \
    bash "$helper" link-connect source target tcp 22 10 >/dev/null 2>"$list_error"; then
    printf '%s\n' "policy helper unexpectedly accepted a malformed Node status" >&2
    exit 1
else
    [[ $? -eq 78 ]]
fi
[[ "$(<"$list_error")" == 'policy helper: invalid mesh node status: target' ]]

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
missing_mesh="$reconcile_marker-missing/mesh/reconcile-requested"
[[ "$(SMM_POLICY_TESTING=1 SMM_POLICY_FLOCK=true SMM_POLICY_RECONCILE_MARKER="$missing_mesh" \
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
rm -f -- "$firewall_error" "$reconcile_marker" "$policy_state" \
    "$policy_listing" "$empty_listing" "$list_error" "$inactive_state"

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
