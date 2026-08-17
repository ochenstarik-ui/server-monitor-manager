#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
bootstrap="$root/deploy/ochenstarik-server-monitor-manager.sh"
fixture="$(mktemp -d -t smm-mesh-permissions.XXXXXXXX)"
test_user="$(id -un)"
test_group="$(id -gn)"
mesh_dir="$fixture/state/mesh"
wg_dir="$fixture/etc/wireguard"

cleanup() {
    sudo rm -rf -- "$fixture"
}
trap cleanup EXIT

extract_function() {
    local name="$1"
    awk -v name="$name" '
        $0 == name "() {" { capture=1 }
        capture && $0 != name "() {" && /^[A-Za-z_][A-Za-z0-9_]*\(\) \{$/ { exit }
        capture { print }
    ' "$bootstrap"
}

ensure_definition="$(extract_function ensure_mesh_state)"
repair_definition="$(extract_function repair_mesh_state_permissions)"
runner="$fixture/apply-permissions.sh"
{
    printf '%s\n%s\n' "$ensure_definition" "$repair_definition"
    printf '%s\n' 'ensure_mesh_state'
} >"$runner"

sudo env MESH_DIR="$mesh_dir" CONTROL_USER="$test_group" bash "$runner"
[[ "$(sudo stat -c '%a:%U:%G' "$mesh_dir")" == "770:root:$test_group" ]]
[[ "$(sudo stat -c '%a:%U:%G' "$mesh_dir/nodes.tsv")" == "660:root:$test_group" ]]

printf '%s\n' $'fixture-node\t10.77.0.2\t-\treserved' >>"$mesh_dir/nodes.tsv"
grep -Fq 'fixture-node' "$mesh_dir/nodes.tsv"

sudo install -d -m 0700 -o root -g root "$wg_dir"
printf '%s\n' 'private-hub-key' | sudo tee "$wg_dir/hub.key" >/dev/null
sudo chown root:root "$wg_dir/hub.key"
sudo chmod 0600 "$wg_dir/hub.key"
if sudo -u "$test_user" test -r "$wg_dir/hub.key"; then
    printf '%s\n' 'Control-equivalent user can read the Hub private key' >&2
    exit 1
fi

sudo chmod 0700 "$mesh_dir"
sudo chmod 0600 "$mesh_dir/nodes.tsv"
{
    printf '%s\n%s\n' "$ensure_definition" "$repair_definition"
    printf '%s\n' 'repair_mesh_state_permissions'
} >"$runner"
sudo env MESH_DIR="$mesh_dir" CONTROL_USER="$test_group" bash "$runner"
[[ "$(sudo stat -c '%a:%U:%G' "$mesh_dir")" == "770:root:$test_group" ]]
[[ "$(sudo stat -c '%a:%U:%G' "$mesh_dir/nodes.tsv")" == "660:root:$test_group" ]]

printf '%s\n' 'MESH_STATE_PERMISSIONS=PASS'
