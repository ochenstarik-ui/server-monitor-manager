#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
base_image="${1:?usage: run-systemd-container-smoke.sh BASE_IMAGE ARCHIVE BOOTSTRAP}"
archive="$(realpath "${2:?archive is required}")"
bootstrap="$(realpath "${3:?bootstrap is required}")"
name="smm-systemd-${RANDOM}-${RANDOM}"
image="smm-systemd-smoke:${base_image//[:\/]/-}"
port="17443"
smoke_dir="/root/smm-smoke"
remote_archive="$smoke_dir/release.tar.gz"
remote_bootstrap="$smoke_dir/ochenstarik-server-monitor-manager.sh"
system_bootstrap="/usr/local/sbin/ochenstarik-server-monitor-manager.sh"

cleanup() {
    docker rm -f "$name" >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker build \
    --build-arg "BASE_IMAGE=$base_image" \
    -f "$root/tests/bootstrap/systemd-container.Dockerfile" \
    -t "$image" \
    "$root"
docker run --detach --privileged --cgroupns=private \
    --tmpfs /run --tmpfs /run/lock --name "$name" "$image" >/dev/null

for _ in {1..30}; do
    if docker exec "$name" systemctl is-system-running >/dev/null 2>&1; then
        break
    fi
    state="$(docker exec "$name" systemctl is-system-running 2>/dev/null || true)"
    [[ "$state" == "degraded" ]] && break
    sleep 1
done
state="$(docker exec "$name" systemctl is-system-running 2>/dev/null || true)"
[[ "$state" == "running" || "$state" == "degraded" ]] || {
    docker exec "$name" systemctl --failed --no-pager || true
    printf '%s\n' "container systemd did not finish booting: $state" >&2
    exit 1
}

docker exec "$name" install -d -m 0700 "$smoke_dir"
docker cp "$archive" "$name:$remote_archive"
docker cp "${archive}.sha256" "$name:${remote_archive}.sha256"
docker cp "$bootstrap" "$name:$remote_bootstrap"
docker exec "$name" chmod 0700 "$remote_bootstrap"

# Copy manifest+sig if available; otherwise install-control will need SMM_ALLOW_UNSIGNED
archive_dir="$(dirname "$archive")"
smm_env=()
if [[ -f "$archive_dir/server-monitor-manager-manifest.json" && -f "$archive_dir/server-monitor-manager-manifest.sig" ]]; then
    docker cp "$archive_dir/server-monitor-manager-manifest.json" "$name:$smoke_dir/server-monitor-manager-manifest.json"
    docker cp "$archive_dir/server-monitor-manager-manifest.sig" "$name:$smoke_dir/server-monitor-manager-manifest.sig"
else
    smm_env=(env SMM_ALLOW_UNSIGNED=1)
fi
docker exec "$name" "$remote_bootstrap" preflight
docker exec "$name" "${smm_env[@]}" "$remote_bootstrap" install-control \
    "$remote_archive" 127.0.0.1 "$port"
docker exec "$name" curl --fail --silent --show-error --retry 15 --retry-all-errors --retry-delay 1 \
    --cacert /etc/ochenstarik-server-monitor-manager/control-ca.crt \
    "https://127.0.0.1:$port/healthz"
docker exec "$name" "${smm_env[@]}" "$remote_bootstrap" install-control \
    "$remote_archive" 127.0.0.1 "$port"

docker restart "$name" >/dev/null
for _ in {1..60}; do
    if docker exec "$name" systemctl is-active --quiet ochenstarik-smm-control.service; then
        break
    fi
    sleep 1
done
docker exec "$name" systemctl is-active --quiet ochenstarik-smm-control.service
docker exec "$name" curl --fail --silent --show-error --retry 15 --retry-all-errors --retry-delay 1 \
    --cacert /etc/ochenstarik-server-monitor-manager/control-ca.crt \
    "https://127.0.0.1:$port/healthz"
docker exec "$name" /usr/local/sbin/ochenstarik-smm-emergency status

# Exercise both removal depths with live services, owned network objects, and
# similarly placed foreign objects that must survive.
docker exec "$name" install -d -m 0700 /var/lib/ochenstarik-server-monitor-manager/bootstrap-backups
docker exec "$name" touch /var/lib/ochenstarik-server-monitor-manager/bootstrap-backups/smoke-preserve
docker exec "$name" useradd --system --user-group --no-create-home ochenstarik-smm-agent
docker exec "$name" useradd --system --user-group --no-create-home ochenstarik-monitor
docker exec "$name" install -d -m 0700 -o ochenstarik-monitor -g ochenstarik-monitor /var/lib/ochenstarik-monitor
docker exec "$name" useradd --system --no-create-home smm-uninstall-foreign
docker exec "$name" sh -c "printf '[Unit]\nDescription=Foreign smoke unit\n' >/etc/systemd/system/smm-uninstall-foreign.service"
docker exec "$name" ip link add smm0 type dummy
docker exec "$name" nft add table inet ochenstarik_smm

docker exec "$name" "$system_bootstrap" uninstall-system --confirm-uninstall
docker exec "$name" test -s /var/lib/ochenstarik-server-monitor-manager/control/control.db
docker exec "$name" test -f /var/lib/ochenstarik-server-monitor-manager/bootstrap-backups/smoke-preserve
docker exec "$name" test -f /etc/ochenstarik-server-monitor-manager/control-ca.pfx
docker exec "$name" test -f /etc/ochenstarik-server-monitor-manager/control-ca.crt
docker exec "$name" id smm-uninstall-foreign
docker exec "$name" test -f /etc/systemd/system/smm-uninstall-foreign.service
docker exec "$name" sh -c "! ss -H -ltn 'sport = :$port' | grep -q ."
for owned_user in ochenstarik-smm-control ochenstarik-smm-agent ochenstarik-monitor; do
    if docker exec "$name" id "$owned_user" >/dev/null 2>&1; then
        printf 'owned user survived uninstall: %s\n' "$owned_user" >&2
        exit 1
    fi
done
for owned_unit in ochenstarik-smm-control.service ochenstarik-smm-agent.service \
    ochenstarik-smm-provisioning-helper.service ochenstarik-smm-firewall.service; do
    if docker exec "$name" test -e "/etc/systemd/system/$owned_unit"; then
        printf 'owned unit survived uninstall: %s\n' "$owned_unit" >&2
        exit 1
    fi
done
if docker exec "$name" ip link show smm0 >/dev/null 2>&1; then
    printf '%s\n' 'owned smm0 interface survived uninstall' >&2
    exit 1
fi
if docker exec "$name" nft list table inet ochenstarik_smm >/dev/null 2>&1; then
    printf '%s\n' 'owned nftables table survived uninstall' >&2
    exit 1
fi

docker exec "$name" "$remote_bootstrap" uninstall-system --confirm-uninstall \
    --purge-data --confirm-destroy-data
for owned_path in /etc/ochenstarik-server-monitor-manager \
    /var/lib/ochenstarik-server-monitor-manager \
    /var/lib/ochenstarik-server-monitor-manager-enrollment \
    /var/lib/ochenstarik-monitor; do
    if docker exec "$name" test -e "$owned_path"; then
        printf 'owned path survived purge: %s\n' "$owned_path" >&2
        exit 1
    fi
done
docker exec "$name" id smm-uninstall-foreign
docker exec "$name" test -f /etc/systemd/system/smm-uninstall-foreign.service
empty_uninstall_output="$(docker exec "$name" "$remote_bootstrap" uninstall-system \
    --confirm-uninstall --purge-data --confirm-destroy-data)"
grep -Fq 'nothing installed' <<<"$empty_uninstall_output"

printf '%s\n' "SYSTEMD_CONTAINER_SMOKE=PASS image=$base_image"
